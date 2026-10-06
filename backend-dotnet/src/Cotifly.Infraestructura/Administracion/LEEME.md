# Infraestructura / Administracion

Administración de políticas de margen por línea de negocio (rol `gerente_comercial`).

- **En esta capa:** Adaptadores que implementan los puertos del módulo: EF Core, clientes HTTP, PDF, correo, proveedores de IA.
- **Subcarpetas:** `Repositories/` (implementaciones EF Core de `ICls*Repository`), `Services/` (adaptadores externos: clientes HTTP, generadores, transportes).

Convenciones y reglas de dependencia: `specs/decisiones/ADR-002-arquitectura-clean-monolito-modular.md`. Este archivo se borra cuando llegue el primer código del módulo.
