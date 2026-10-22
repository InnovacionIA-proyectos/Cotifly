# Pendientes de backend para Duban — CERRADO (2026-10-22)

**Fecha original:** 2026-10-20 · **Repo:** `Cotifly`.

> **Estado final (2026-10-22):** los 5 temas de este documento ya se resolvieron. Dos quedaron superados por issues que ya existían en GitHub (no se crearon duplicados); los otros 3 ya están creados como issues reales. Este archivo queda solo como historial — lo vigente está en GitHub.

---

## Resultado de cada tema

| Tema original | Resultado |
|---|---|
| 1. Detalle regional + Excel para `gerente_comercial` | Creado: milestone [HU-034](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/26), issue [#201](https://github.com/InnovacionIA-proyectos/Cotifly/issues/201) (Parte A) |
| 2. `serie_mensual` sin ganancias/costeo por mes | Creado: mismo issue [#201](https://github.com/InnovacionIA-proyectos/Cotifly/issues/201) (Parte B) |
| 3. Correo/D3 — candidato MailSender API | **No se creó nada — ya existía.** Superado por [HU-033 / #199](https://github.com/InnovacionIA-proyectos/Cotifly/issues/199), "Decisiones del líder técnico (2026-10-22)": adopta MailSender, protección fuera de Production (buzón `[QA]`), precio sin desglose, resultado diferido a NEXO. |
| 4. Endpoint de líneas de negocio para Comercial | **No se creó issue nueva — se solapaba.** Se agregó como [comentario en #154](https://github.com/InnovacionIA-proyectos/Cotifly/issues/154#issuecomment-6346132852) (HU-016.1, Parte B "Opción A"), que ya proponía casi lo mismo para otro caso de uso (margen promedio). Ese issue además aclara que el margen **no** es confidencial para Comercial (ya se expone vía el chat, herramienta `obtener_margen`) — contradice la suposición inicial de este documento. |
| 5. Importar mercancías de una ruta desde Excel | Creado: milestone [HU-035](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/27), issue [#202](https://github.com/InnovacionIA-proyectos/Cotifly/issues/202) |

## Hallazgo adicional, importante

Al verificar contra GitHub antes de crear estos issues, apareció **[HU-030 / #184](https://github.com/InnovacionIA-proyectos/Cotifly/issues/184)** ("Peso con tara en lo que Cotifly manda a Nexo", decisión D2/D2b, no construida todavía): el Comercial debe escribir el peso **con tara** y ese valor (no el neto) se manda a NEXO como peso de la línea. Esto contradice una respuesta que se le había dado a `clientepro-frontend-fe` sobre `pesoKg` (se le corrigió directamente). Si se construye algo relacionado con peso/tara, coordinarlo con esa issue en vez de resolverlo por separado.
