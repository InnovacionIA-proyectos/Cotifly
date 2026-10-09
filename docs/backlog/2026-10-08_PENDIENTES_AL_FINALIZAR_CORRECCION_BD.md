# Pendientes para cuando se finalice la corrección de la base de datos (HU-021)

**Fecha:** 2026-10-08. Cosas que quedaron fuera de las HU a propósito o que dependen de otras personas. Revisar al cerrar HU-021.

## De HU-021.2 (base de datos)

- [ ] **`TrqType` de SAP para `DevolucionContenedor`.** Lo da negocio o SAP. Sembrarlo en `db/seeder/02_sap_mapeo_trqtype.sql` (hoy hay 5 mapeos; falta este). No se inventa.
- [x] `db/migrations/` (Alembic) y `alembic.ini`: borrados (2026-10-08).
- [ ] **Resto del stack viejo de Postgres**, sin tocar y todavía apuntando a Alembic o a Postgres: `orquestador/Dockerfile`, `scripts/entrypoint_orquestador.sh`, `scripts/verificar_bd.ps1`, `tests/conftest.py`, `tests/test_db.py`, `pyproject.toml`, `mcp_server/`, `orquestador/`, `shared/`, `tests/`, y los servicios `bot-db` de `deploy/docker-compose.swarm.yml`, `infra/stack.dokploy.yml` e `infra/stack.staging.yml`. Decidir si se borran también.
- [x] `ALTER` del `CHECK` de `estadoEnvio` en la base compartida: ya corrido (2026-10-08).

## Del git y los PR

- [ ] Al abrir el PR de HU-021.2 (rama 129), poner como **base la rama 118**, no QA.
- [ ] Actualizar la descripción del PR de la 118: migración `0008` → `0009`, horas por línea, el `0001` no se tocó.
- [ ] Los 6 archivos `.cs` de Nexo de Duban siguen sin comitear (`clsCatalogosMaestrosNexo`, `clsBuscadorClientesNexo`, `clsCosteoNexo`, `clsCubicacionNexo`, `clsAnalizadorExcelNexo`, `clsMunicipiosNexo`).

## Del backend (issues siguientes de HU-021)

- [ ] Adaptar el backend al esquema nuevo (HU-021.3 en adelante). Detalle en `docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md`.
- [ ] ~28 archivos de `backend-dotnet/` todavía apuntan a rutas viejas de la documentación (ver `2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`, punto 11).
- [ ] Comunicación Cotifly ↔ cubicador (`POST /recomendaciones`, `GET /tipos-mercancia`, `GET /tipos-carroceria`): nombres de campos, autenticación y fechas. Decisiones ya tomadas con Brayan: Cotifly manda el `idTipoMercancia` y guarda id + nombre (sin código estable); si hay que re-cubicar una cotización vieja y el id ya no existe, se busca por nombre y, si no hay coincidencia, el comercial la vuelve a escoger. La mercancía condicionada vuelve como resultado marcado, sin flujo de aprobación en el cubicador para Cotifly.

## Limpieza al terminar

- [ ] En la base compartida: borrar `dbo_cotifly_respaldo_20261008` cuando ya no se necesite.
- [ ] Las tablas `Respaldo0009_*` solo existen si algún día se aplica el `0009` (hoy es respaldo). Si se aplicara, borrarlas cuando el backend nuevo esté validado.
- [ ] Apagar el contenedor local `cotifly-sqlserver-1` (puerto 1434) que se usó para las pruebas.
