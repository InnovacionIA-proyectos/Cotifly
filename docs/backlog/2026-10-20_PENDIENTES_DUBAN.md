# Pendientes de backend para Duban

**Fecha:** 2026-10-20 · **Repo:** `Cotifly`.

---

## 1. Abrir el detalle completo y el Excel de licitación a `gerente_comercial` para cotizaciones de su regional

**Origen:** HU-021-FE.7 / vista regional "Cotizaciones" del Gerente Comercial (`clientepro-frontend`), 2026-10-20.

**Contexto:** se agregó "Ver detalle" de solo lectura a la vista regional (un gerente viendo el resumen de una cotización de un comercial de su regional). Se verificó contra `openapi.yaml` (`origin/QA`) que dos endpoints son **"solo el dueño"**, sin excepción para ningún otro rol:

1. `GET /web/cotizaciones/{folio}/costeo` — de ahí sale el detalle rico: rutas, camiones, servicios conexos, margen ajustable, textos. Responde 404 `NoEsTuya` a cualquiera que no sea el comercial dueño.
2. `GET /web/licitaciones/{folio}/excel` — "Solo el dueño **y** solo licitaciones" (ni el propio comercial lo tiene para cotizaciones normales).

**Por eso, mientras tanto,** el "Ver detalle" del gerente quedó armado solo con lo que ya trae `GET /web/cotizaciones/regional` (folio, cliente, línea de negocio, precio, margen, estado, fecha, deadline, score) más el botón "Ver PDF", que sí funciona porque ya tiene este mismo patrón resuelto (ver abajo). Sin rutas, camiones, conexos, margen histórico, textos ni Excel.

**Lo que se le pide a Duban, si negocio quiere el detalle completo más adelante:**
- [ ] Abrir `GET /web/cotizaciones/{folio}/costeo` a `gerente_comercial` para cotizaciones de comerciales de **su misma regional**.
- [ ] Abrir `GET /web/licitaciones/{folio}/excel` con la misma regla, cuando la cotización sea una licitación.
- [ ] **Reusar el patrón que ya existe** para la descarga de PDF (`clsDescargaPdfService`): el gerente solo accede si el dueño es miembro de su regional efectiva (`IClsRegionalUsuario.EsMiembroDeMiRegionalAsync`, `DimEjecutivo` primero, `UsuariosWeb.regional` de respaldo — mismo criterio de `GET /web/cotizaciones/regional` y `GET /web/metricas/regional`, ADR-016, HU-016.1 #154). Si el gerente no tiene regional o la fuente está caída, negar con 404 (fail-closed): solo el dueño.
- [ ] Auditar cada acceso de un gerente a una cotización ajena, igual que ya hace la descarga de PDF.
- [ ] Actualizar `openapi.yaml` (quitar "solo el dueño" sin matizar) y `docs/docs_front/CONTRATO_API_WEB.md` / `CONTRATOS_API_COTIFLY_FRONTEND.md`.

También quedó anotado del lado del frontend en `docs/COTIFLY_CONSTRUCCION.md` (sección "Gerente Comercial: Todas las cotizaciones conectada...").
