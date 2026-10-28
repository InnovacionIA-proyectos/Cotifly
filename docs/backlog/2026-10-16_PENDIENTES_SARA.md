# Pendientes de Sara (Datos y Frontend)

**Fecha:** 2026-10-16 · **Revisado contra:** `origin/QA` hasta #177 y la base compartida · **Repo:** `Cotifly`.

Solo lo que le toca a Sara, de lo más urgente a lo menos urgente. El contexto completo está en `docs/backlog/2026-10-14_HU_Correcciones_Nuevas_Cotifly.md`. Lo de Duban está en `docs/backlog/2026-10-16_PENDIENTES_DUBAN.md`.

---

## 🔴 Urgente

### 1. Aplicar el `07` (motivo `BUSQUEDA_AGOTADA`) en la base compartida
- [x] Traer `QA`: ya trae `db/scripts/07_motivo_busqueda_agotada.sql`, de Duban (#176). Hecho el 2026-10-16.
- [ ] Borrar el `db/scripts/pendientes/07_motivo_busqueda_agotada.sql` local (sin commit). Sobra: el que se usa es el de Duban, que aplica la misma regla (5 motivos y conserva el `ISNULL`).
- [ ] Correrlo en la compartida:
  ```
  sqlcmd -S 10.10.40.85,1433 -d dbTarifario_Nexo_Pruebas -E -C -I -b -f 65001 -i db/scripts/07_motivo_busqueda_agotada.sql
  ```
  Hoy el CHECK no admite `BUSQUEDA_AGOTADA`, y no hay filas que lo bloqueen. Sin el `07` el backend guarda el motivo en NULL y lo deja solo como advertencia.

### 2. Pasarle a Duban sus pendientes
- [ ] `docs/backlog/2026-10-16_PENDIENTES_DUBAN.md`, con lo más urgente primero: **CORS** (bloquea hoy las pruebas del frontend en el navegador).
- [ ] El parche de HU-010.5: `docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`.

---

## 🟠 Alta

### 3. 10 cotizaciones de prueba para el tablero del Gerente Comercial
- [ ] Dar las indicaciones para volver a cargarlas en la compartida. Ya se puede: al 2026-10-16 la compartida tiene 1 usuario en `UsuariosWeb` y 3 cotizaciones.

### 4. Backlog de NEXO
- [ ] **Confirmar en GitHub que NEXO#297 (S5-HU-20.5) quedó recortada** a dos puntos: (a) `GET /api/solicitudes/{id}` devuelve `numeroLinea` y ordena por él; (b) una línea de Cupo sin vehículo no se autocostea. Lo de `grupoRutaId`, `idRecomendacionVehiculo`, la cuarentena y el adaptador ya está hecho (18.1 a 18.4).
- [ ] **Confirmar que sigan abiertas en el portal (`administracion_embarqa_nexo`) las issues de S5-HU-19.3 y 19.4:**
  - La bandeja de normalización de vehículos en cuarentena no existe; el commit `b6f7070` dice "sin bandeja de normalización".
  - No hay rastro de editar el vehículo de una línea ni de guardar el precio sin vehículo en la Mesa.
  - Sin esas dos, una línea "sin selección" o con un vehículo en cuarentena se queda sin resolver en la Mesa.
- [ ] **Que se fusione la #289 del portal** (`289-mesa-costeo-agrupar-por-ruta`, la Mesa agrupada por ruta). Está hecha, pero no está en QA.

### 5. Issues del frontend que hay que corregir (comentario en la issue, sin editar el cuerpo)
Hallazgos del 2026-10-16 al construir el frontend contra el contrato real de QA:
- [ ] **#69 (HU-021-FE.1):** "ruta sin camiones" no es un error. Si el cubicador falla con respaldo, o no escoge vehículo, la ruta queda con un camión "Vehículo por definir" que resuelve la Mesa. Se muestra como aviso, no como error.
- [ ] **#70 (HU-021-FE.2):** con `Captura:AceptarVehiculoCliente` apagada, el vehículo digitado no tiene efecto (Duban confirma los ambientes). La bandera del frontend va como constante local ("TEMPORAL, se borra al publicar #81").
- [ ] **FE.4 (servicios conexos):** la regla es la inversa de lo que dice la issue. Agregar (`POST`) exige que la ruta ya tenga línea en NEXO (409 `LINEA_SIN_COTIZACION_NEXO`). Quitar o cambiar cantidad (`PUT`) se permite siempre.
- [ ] **#73 (HU-021-FE.5):** quitar el desglose de costos (fletes, retorno, levante, costo final). El contrato no expone costos de NEXO por diseño (ADR-015/018); solo precios con margen.
- [ ] **HU-021-FE.6 (licitación):** el backend ya tiene subir el Excel, crear la licitación, consultarla y exportarla. No hay que parsear el Excel en el navegador. Los conexos se definen al crear; ya no existe "excluir" un conexo. La dependencia real es HU-011.2/011.3 y #160, no HU-021.6.
- [ ] **#76 (HU-021-FE.8):** la nueva versión va por `POST .../costeo/re-solicitar` (versión `-vN`), no por "Nueva cotización". Con varias rutas solo se puede crear una versión idéntica. El correo no muestra el total.
- [ ] **#80 (HU-023-FE.1):** tenía fecha del 16 de octubre. Confirmar la nueva fecha: depende de que `/recomendaciones` esté en el QA del cubicador.

### 5.1 Importador de licitaciones (frontend): unidad del peso
Decisión de Sara (2026-10-16), opción A:
- [ ] En el paso de mapeo de columnas del importador, un **selector kg/t** para la columna de peso.
- [ ] Un parser en el frontend que separa el número de la unidad: "18" → según el selector; "18t" o "18 t" → 18 000 kg; "18000 kg" → 18 000.
- [ ] Al backend siempre se manda `peso_kg` en kilos. El backend no cambia: valida el rango (0 a 999.999,99) y la regla de peso.
- El diccionario de nombres de vehículo vive en NEXO (decisión de Sara). Cotifly hoy ignora el vehículo del Excel (decisión 7 de HU-021), así que no hay nada que hacer por ahora.

### 6. Cubicador (Brayan)
- [ ] Que Brayan corrija el texto de S5-HU-17.4: `POST /recomendaciones` lleva `codigoMercancia`, no `idTipoMercancia` (decidido el 2026-10-14).

---

## 🟡 Media

### 7. Rol `comercial` de 34 a 32 — tu parte
Va junto con el cambio de la base de identidad a `dbSolucionesTI_Prueba` (#172). Son tres partes en una sola ventana: base (DBA), configuración (Duban) y frontend (tú).
- [ ] **Confirmar los números** con alguien que tenga acceso a `dbSolucionesTI_Prueba`: que ahí el 32 es `ClienteProComercial` y el 31 es `ClienteProGerencial`. Hoy solo lo dice el script de Duban.
- [ ] **Frontend:** cambiar `cotifly-roles.ts`, que hoy reconoce como comercial el 34 (y el 25 en pruebas), al 32, y revisar qué es el 25 en la base nueva. Va en la misma ventana que el cambio de la base, no antes.
- [ ] Coordinar con Duban y el DBA la ventana del cambio.

### 8. TrqType de SAP para `DevolucionContenedor`
- [ ] Pedírselo a negocio o a SAP; no se inventa.
- [ ] Cuando llegue, sembrarlo en `db/seeder/02_sap_mapeo_trqtype.sql` y en la compartida.

### 9. Script `06` (idempotencia de la captura)
- [ ] No aplicarlo todavía. Aplicarlo en la compartida cuando Duban avise que el backend usa la tabla `CapturasIdempotentes`.

---

## ⚪ Baja

### 10. HU-024 — Organizar los scripts de base de datos
La HU completa (historia, criterios e issues) está en `docs/backlog/2026-10-14_HU_Correcciones_Nuevas_Cotifly.md`, punto 4.1.
- [ ] **HU-024.1:** mover `03`, `04` y `05` de `db/scripts/pendientes/` a `db/scripts/` (el `07` ya está ahí). Decidir dónde queda el `0010`, probar el ejecutor en Docker y actualizar las referencias.
- [ ] **HU-024.2:** consolidar `02`, `03`, `04`, `05` y `07` en el `01`. Comparar en Docker el esquema de "solo `01`" contra "`01` + ajustes".

### 11. Limpieza local
- [ ] Borrar la carpeta `tests/` sin seguimiento: son 4 CSV de tarifarios del stack viejo de Python, que quedaron después de la limpieza de HU-022.1 (#171).
- [ ] Revisar los stash:
  - `stash@{0}`: lo retirado el 2026-10-16. El header ya está en QA, y la HU-010.5 está en el parche.
  - `stash@{1}`: el header local viejo. Se puede borrar.
  - `stash@{2}`: el WIP "DatosNuevaVersion ampliado" (HU-004.4). **Sirve para el punto 7.2 de Duban:** pasárselo antes de borrarlo.
- [ ] Decidir si se suben los documentos de `docs/backlog/` (este, el de Duban, el de correcciones) y la carpeta `parches/`, o se quedan locales.
