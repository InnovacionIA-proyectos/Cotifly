# ADR-030 — Política de seguros por ruta en el costeo y bloqueo de "Finalizar" (HU-046.3)

- **Estado:** Propuesta (2026-11-08). Issue #269 (HU-046.3), milestone HU-046. Pendiente del visto bueno del usuario (ver Pendientes).
- **Relacionadas:** HU-046.1 (#267, decisiones y carga aprobada: `docs/backlog/2026-11-02_DECISIONES_HU-046_POLITICA_SEGUROS.md`),
  HU-046.2 (#268, script `db/scripts/21_politica_seguros.sql`), HU-036.2 (#206, "Finalizar"), #228 y #259 (conexos pendientes en Nexo),
  ADR-029 (seguro de la mercancía por ruta).

## Contexto

La política de seguros NOGE0076 define tramos por el valor de la mercancía y, en cada uno, medidas recomendadas u obligatorias. Datos ya
creó las tablas (`PoliticaSeguroTramos`, `PoliticaSeguroMedidas`, `PoliticaSeguroTramoMedidas`, `PoliticaSeguroMedidaConexos`) con la
carga aprobada y la columna `CotizacionServiciosConexos.codigoServicio` (script 21). Las reglas viven en los datos: el backend no debe
saber qué medida es un acompañante; solo cruza los `codigoServicio` de los conexos de la ruta contra los de cada medida.

## Decisión

1. **Dominio puro** (`Dominio/Seguros/clsPoliticaSeguros`): tramo por el `valor_mercancia` TOTAL de la ruta (decisión 2), `valorDesde`
   exclusivo y `valorHasta` incluido (decisión 3); sin valor, valor 0 o negativo: sin tramo y con aviso, no bloquea (decisión 4). Una
   obligatoria se cumple con CUALQUIERA de sus conexos; una obligatoria sin conexos se muestra como recomendada (nunca queda pendiente).
   Los códigos se comparan sin distinguir mayúsculas.
2. **Persistencia de solo lectura y sin caché** (`clsPoliticaSegurosRepository`, filas `clsPoliticaSeguro*Fila`, `AsNoTracking`): tres
   consultas por lectura (tramos activos; reglas con medidas activas; conexos de esas medidas). Un cambio de Datos se ve en la siguiente
   petición. El modelo EF describe PK, FK, índices, UNIQUE y CHECK del 21 (prueba de deriva); el 21 entra al arnés de pruebas detrás del
   20 y a `/health/ready` ("falta 21").
3. **`codigoServicio` del conexo:** se guarda al crearlo desde el catálogo (captura, `POST .../servicios-conexos/lineas`, re-solicitud y
   versión nueva: `clsCostoConexoNexo.Codigo`, leído de `codigoServicio`). En `POST .../servicios-conexos` (que importa la fila de la
   cotización de Nexo, sin código) sale del índice en memoria del catálogo (abajo), sin leer el catálogo de Nexo ni llenar la caché de
   costos (que decide qué servicios resuelve la versión nueva); con el índice frío se completa en la siguiente lectura. Las filas en NULL
   (anteriores al 21 o importadas por el polling) se completan con el índice del catálogo que ya mantiene el costeo para las categorías
   (`IClsCategoriasConexos.CodigosAsync`, calentado en segundo plano): en `GET .../costeo` se guardan sin subir la versión de negocio (si
   el guardado falla, se deja en el log y se completa en otra lectura) y en "Finalizar" antes de evaluar.
4. **Costeo:** `rutas[].politica_seguros` (`{tramo, medidas_recomendadas, medidas_obligatorias, aviso}`) en `GET .../costeo`, en el
   detalle por folio (sin completar códigos) y en las respuestas de conexos, margen y seguro (las que usan `VistaConEstadosAsync`). `null` en una ruta rechazada, en una cotización
   finalizada (decisión 7: no se recalcula) y si la política no se pudo leer (la vista sale igual).
5. **Finalizar:** después de los bloqueos existentes (estado, conexos pendientes de leer, conexos pendientes en Nexo), si una ruta
   cotizable tiene una obligatoria pendiente: 409 `COTIZACION_NO_FINALIZABLE` con `detalle.motivo = medidas_obligatorias_faltantes` y
   `detalle.rutas`. Cuerpo opcional `{justificacion}`: no vacía y hasta 1000 caracteres (400 si no cabe). **La justificación salta SOLO
   este bloqueo** (decisión del usuario, 2026-11-06). Se audita en `EventosHerramienta` (`finalizar_cotizacion`): la justificación en la
   entrada y, si saltó el bloqueo, `medidas_omitidas` (códigos por ruta) en la salida; sin montos. Si la política no se puede leer,
   "Finalizar" falla (no se finaliza sin evaluarla). Re-finalizar una finalizada no lee la política.

6. **Observaciones de la auditoría (2026-11-09):**
   - B1: el `GET .../costeo` guarda los códigos completados fila por fila (`UPDATE … WHERE id = … AND codigoServicio IS NULL`,
     `IClsCotizacionRepository.GuardarCodigosServicioAsync`), fuera del rastreador: no toma el candado de la cabecera, no sube la versión
     ni cambia el `ETag`, y una fila borrada o ya completada por otra petición no se toca.
   - B3: "Finalizar" solo lee la política si no hay conexos pendientes en Nexo. ORDEN con #259: la evaluación va DESPUÉS de
     `ConexosPendientesEnNexoAsync` (que guarda sin versión); invertirlo guardaría códigos completados sin la versión del `If-Match`.
   - B4: política ilegible al finalizar: 503 `SERVICIO_OCUPADO` con `detalle.motivo = politica_seguros_no_disponible`, auditado. Sigue
     fallando cerrado.
   - B6: con el índice del catálogo vigente no se recalienta por un código que no está en él (solo con el índice frío o vencido).
   - B7: `politica_seguros.aviso_codigo` (`sin_valor_mercancia` | `sin_tramo`).
   - B8: `valor_mercancia` y los límites de los tramos están siempre en pesos (COP), también en cotizaciones en USD.
7. **Decisiones del usuario (2026-11-09):**
   - Un conexo cuyo envío a Nexo quedó `fallido_definitivo` o `incierto` **no** cumple ninguna medida; solo cuentan `enviado` y
     `pendiente` (este último lo bloquea #228 aparte). Se aplica en el costeo (con el `estado_nexo` de la vista) y en "Finalizar".
   - Licitaciones de 350 M o más: se quedan como están. No admiten conexos (HU-041.1), así que se finalizan con justificación.

## Consecuencias

- Cambiar la política no requiere desplegar; los tramos y medidas inactivos no cuentan.
- `GET .../costeo` puede escribir (solo `codigoServicio` de filas en NULL), sin tocar la versión de negocio ni el precio.
- Con el índice del catálogo frío, un conexo antiguo sin código no cumple todavía: el panel lo muestra pendiente hasta la siguiente
  lectura y "Finalizar" lo bloquea (falla cerrado; la justificación lo salta).

## Pendientes de decisión

- Largo de la justificación: se fijó un máximo de 1000 caracteres y ningún mínimo (basta con que no esté vacía).
- Orden de las medidas en el panel: por código (las tablas no tienen columna de orden).
