"""HU-002.2 — Ejecuta la propuesta de reestructuración aprobada en HU-002.1.

Cambios de limpieza (sin dependencia externa):
- Elimina `cotizaciones.payload` (JSONB sin consumidor real) y `sesiones.contexto`
  (JSONB que nunca se lee).
- Elimina el índice `ix_eventos_herr_thread` (nadie filtra por ahí hoy).
- Agrega `CHECK (estado IN ('activa','archivada'))` en `sesiones`.
- Agrega la FK `eventos_conversacion(space_id,thread_id) -> sesiones(...)` (segura,
  verificada 52/0 huérfanos) y `cotizaciones.linea_negocio -> margenes_politica.linea_negocio`
  (segura, verificada 1/0 huérfanos en dev).

Cambios que anticipan la integración con Nexo (HU-005) y ClientePro (HU-004), ejecutados
ya por decisión explícita del equipo (2026-09-22) aunque sus consumidores todavía no
existen:
- `cotizaciones`: elimina `origen`, `destino`, `fuente_tarifa`; divide `estado` en
  `estado_costeo`/`estado_envio`; agrega `costeo_id`, `bd_cliente`, `linea_negocio_id`.
- `margenes_politica` -> gana `id` (nueva PK serial) y `is_active`; `linea_negocio` pasa
  de PK a UNIQUE.
- `usuarios_web`: gana `id` (entero, el mismo id de SAP, no autoincremental) y `telefono`;
  pierde `email`/`nombre`. Se vacía (`TRUNCATE`) porque ninguna fila existente tiene un id
  de SAP real que asignarle -- decisión aceptada: se pierde el dato de identidad de todo
  usuario que haya iniciado sesión en la Consola web hasta hoy.
- `cotizaciones.comercial_email`, `ajustes_margen.comercial_email`,
  `eventos_conversacion.usuario_email` -> se eliminan y se reemplazan por `usuario_id`
  (entero, FK a `usuarios_web.id`, nullable). `correos_enviados.confirmado_por` cambia de
  texto a entero (mismo nombre). Se acepta perder la trazabilidad histórica de "quién" en
  las 4 tablas: no hay forma de mapear un correo a un id de SAP sin ClientePro (HU-004).

Consecuencia de aplicación (documentada, no se revierte en el código de esta migración):
`/web/login` deja de poder emitir sesiones válidas (no hay de dónde sacar el id de SAP) y
por lo tanto toda la Consola web queda sin mecanismo de identidad hasta HU-004. El flujo de
cotizar por Google Chat no depende de este login y sigue funcionando igual.

Revision ID: 0006_hu002_2_esquema_completo
Revises: 0005_usuarios_y_seguimiento
Create Date: 2026-09-22
"""

from __future__ import annotations

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "0006_hu002_2_esquema_completo"
down_revision: str | None = "0005_usuarios_y_seguimiento"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


_V_AJUSTES = "v_ajustes_margen_por_comercial"
_V_COTIZ = "v_trazabilidad_cotizaciones"
_V_ACTIV = "v_actividad_por_comercial"


def upgrade() -> None:
    # --- 0. Vistas (0003/0004): dependen de columnas que se eliminan/renombran
    # más abajo -- hay que soltarlas antes de tocar esas columnas. `v_trazabilidad_
    # cotizaciones` se descarta (sin consumidor de código, depende de origen/destino/
    # fuente_tarifa que ya no existen); las otras 2 se recrean al final con los
    # nombres nuevos.
    op.execute(f"DROP VIEW IF EXISTS {_V_COTIZ};")
    op.execute(f"DROP VIEW IF EXISTS {_V_ACTIV};")
    op.execute(f"DROP VIEW IF EXISTS {_V_AJUSTES};")

    # --- 1. Limpieza aprobada sin dependencia externa -----------------------
    op.drop_index("ix_eventos_herr_thread", table_name="eventos_herramienta")
    op.drop_column("cotizaciones", "payload")
    op.drop_column("sesiones", "contexto")
    op.create_check_constraint(
        "ck_sesiones_estado", "sesiones", "estado IN ('activa','archivada')"
    )
    op.create_foreign_key(
        "fk_eventos_conv_sesion",
        "eventos_conversacion",
        "sesiones",
        ["space_id", "thread_id"],
        ["space_id", "thread_id"],
    )

    # --- 2. margenes_politica: id serial + is_active, linea_negocio a UNIQUE -
    # SQL directo (no `op.add_column`): SERIAL crea la secuencia, la fija como
    # default y rellena las filas existentes en un solo paso.
    op.execute("ALTER TABLE margenes_politica ADD COLUMN id SERIAL")
    op.add_column(
        "margenes_politica",
        sa.Column("is_active", sa.Boolean(), nullable=False, server_default=sa.text("true")),
    )
    op.drop_constraint("margenes_politica_pkey", "margenes_politica", type_="primary")
    op.create_primary_key("margenes_politica_pkey", "margenes_politica", ["id"])
    op.create_unique_constraint(
        "uq_margenes_politica_linea_negocio", "margenes_politica", ["linea_negocio"]
    )

    # --- 3. cotizaciones.linea_negocio -> margenes_politica.linea_negocio ---
    op.create_foreign_key(
        "fk_cotizaciones_linea_negocio",
        "cotizaciones",
        "margenes_politica",
        ["linea_negocio"],
        ["linea_negocio"],
    )

    # --- 4. cotizaciones: columnas que Nexo reemplaza/alimenta --------------
    op.drop_column("cotizaciones", "origen")
    op.drop_column("cotizaciones", "destino")
    op.drop_column("cotizaciones", "fuente_tarifa")
    op.add_column("cotizaciones", sa.Column("estado_costeo", sa.Text(), nullable=True))
    op.add_column("cotizaciones", sa.Column("estado_envio", sa.Text(), nullable=True))
    op.drop_column("cotizaciones", "estado")
    op.add_column("cotizaciones", sa.Column("costeo_id", sa.Text(), nullable=True))
    op.add_column("cotizaciones", sa.Column("bd_cliente", sa.Text(), nullable=True))
    op.add_column("cotizaciones", sa.Column("linea_negocio_id", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_cotizaciones_linea_negocio_id",
        "cotizaciones",
        "margenes_politica",
        ["linea_negocio_id"],
        ["id"],
    )

    # --- 5. usuarios_web: rediseño completo (se vacía, PK pasa a id de SAP) -
    op.execute("TRUNCATE TABLE usuarios_web")
    op.add_column("usuarios_web", sa.Column("id", sa.Integer(), nullable=True))
    op.add_column("usuarios_web", sa.Column("telefono", sa.Text(), nullable=True))
    op.drop_constraint("usuarios_web_pkey", "usuarios_web", type_="primary")
    op.drop_column("usuarios_web", "email")
    op.drop_column("usuarios_web", "nombre")
    op.alter_column("usuarios_web", "id", nullable=False)
    op.create_primary_key("usuarios_web_pkey", "usuarios_web", ["id"])

    # --- 6. usuario_id (FK a usuarios_web.id) en las 3 tablas de texto libre -
    op.drop_index("ix_cotizaciones_comercial", table_name="cotizaciones")
    op.drop_column("cotizaciones", "comercial_email")
    op.add_column("cotizaciones", sa.Column("usuario_id", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_cotizaciones_usuario", "cotizaciones", "usuarios_web", ["usuario_id"], ["id"]
    )

    op.drop_column("ajustes_margen", "comercial_email")
    op.add_column("ajustes_margen", sa.Column("usuario_id", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_ajustes_margen_usuario", "ajustes_margen", "usuarios_web", ["usuario_id"], ["id"]
    )

    op.drop_column("eventos_conversacion", "usuario_email")
    op.add_column("eventos_conversacion", sa.Column("usuario_id", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_eventos_conv_usuario",
        "eventos_conversacion",
        "usuarios_web",
        ["usuario_id"],
        ["id"],
    )

    # --- 7. correos_enviados.confirmado_por: texto -> entero (mismo nombre) -
    op.drop_column("correos_enviados", "confirmado_por")
    op.add_column("correos_enviados", sa.Column("confirmado_por", sa.Integer(), nullable=True))
    op.create_foreign_key(
        "fk_correos_confirmado_por",
        "correos_enviados",
        "usuarios_web",
        ["confirmado_por"],
        ["id"],
    )

    # --- 8. Recrear las 2 vistas que sobreviven, con los nombres nuevos -----
    op.execute(f"""
        CREATE VIEW {_V_AJUSTES} AS
        SELECT
            a.usuario_id,
            c.linea_negocio,
            COUNT(*)              AS num_ajustes,
            AVG(a.diferencia)     AS diferencia_promedio,
            MIN(a.diferencia)     AS diferencia_min,
            MAX(a.diferencia)     AS diferencia_max,
            MAX(a.created_at)     AS ultimo_ajuste
        FROM ajustes_margen a
        LEFT JOIN cotizaciones c ON c.id = a.cotizacion_id
        GROUP BY a.usuario_id, c.linea_negocio;
    """)
    op.execute(f"""
        CREATE VIEW {_V_ACTIV} AS
        SELECT
            c.usuario_id,
            COUNT(DISTINCT c.id)                                              AS num_cotizaciones,
            COUNT(DISTINCT c.id) FILTER (WHERE c.estado_envio = 'enviada')    AS num_cotizaciones_enviadas,
            COALESCE(SUM(sub.num_ajustes), 0)                                 AS num_ajustes,
            MAX(c.created_at)                                                 AS ultima_cotizacion
        FROM cotizaciones c
        LEFT JOIN (
            SELECT cotizacion_id, COUNT(*) AS num_ajustes
            FROM ajustes_margen
            WHERE cotizacion_id IS NOT NULL
            GROUP BY cotizacion_id
        ) sub ON sub.cotizacion_id = c.id
        WHERE c.usuario_id IS NOT NULL
        GROUP BY c.usuario_id;
    """)


def downgrade() -> None:
    # Revierte las 2 vistas recreadas arriba antes de deshacer las columnas de las
    # que dependen.
    op.execute(f"DROP VIEW IF EXISTS {_V_ACTIV};")
    op.execute(f"DROP VIEW IF EXISTS {_V_AJUSTES};")

    # Nota: revierte estructura, no datos. Los valores de `usuarios_web` (truncada),
    # `comercial_email`/`usuario_email`/`confirmado_por` (eliminados) y las columnas de
    # `cotizaciones` de este downgrade (`origen`/`destino`/`fuente_tarifa`/`estado`) NO
    # se recuperan -- ya no existen en ningún lado a partir del upgrade().
    op.drop_constraint("fk_correos_confirmado_por", "correos_enviados", type_="foreignkey")
    op.drop_column("correos_enviados", "confirmado_por")
    op.add_column("correos_enviados", sa.Column("confirmado_por", sa.Text(), nullable=True))

    op.drop_constraint("fk_eventos_conv_usuario", "eventos_conversacion", type_="foreignkey")
    op.drop_column("eventos_conversacion", "usuario_id")
    op.add_column("eventos_conversacion", sa.Column("usuario_email", sa.Text(), nullable=True))

    op.drop_constraint("fk_ajustes_margen_usuario", "ajustes_margen", type_="foreignkey")
    op.drop_column("ajustes_margen", "usuario_id")
    op.add_column("ajustes_margen", sa.Column("comercial_email", sa.Text(), nullable=True))

    op.drop_constraint("fk_cotizaciones_usuario", "cotizaciones", type_="foreignkey")
    op.drop_column("cotizaciones", "usuario_id")
    op.add_column("cotizaciones", sa.Column("comercial_email", sa.Text(), nullable=True))
    op.create_index("ix_cotizaciones_comercial", "cotizaciones", ["comercial_email"])

    op.drop_constraint("usuarios_web_pkey", "usuarios_web", type_="primary")
    op.drop_column("usuarios_web", "id")
    op.add_column("usuarios_web", sa.Column("email", sa.Text(), nullable=True))
    op.add_column("usuarios_web", sa.Column("nombre", sa.Text(), nullable=True))
    op.execute("DELETE FROM usuarios_web WHERE email IS NULL")
    op.alter_column("usuarios_web", "email", nullable=False)
    op.create_primary_key("usuarios_web_pkey", "usuarios_web", ["email"])
    op.drop_column("usuarios_web", "telefono")

    op.drop_constraint("fk_cotizaciones_linea_negocio_id", "cotizaciones", type_="foreignkey")
    op.drop_column("cotizaciones", "linea_negocio_id")
    op.drop_column("cotizaciones", "bd_cliente")
    op.drop_column("cotizaciones", "costeo_id")
    op.add_column("cotizaciones", sa.Column("estado", sa.Text(), nullable=True))
    op.drop_column("cotizaciones", "estado_envio")
    op.drop_column("cotizaciones", "estado_costeo")
    op.add_column("cotizaciones", sa.Column("fuente_tarifa", sa.Text(), nullable=True))
    op.add_column("cotizaciones", sa.Column("destino", sa.Text(), nullable=True))
    op.add_column("cotizaciones", sa.Column("origen", sa.Text(), nullable=True))

    op.drop_constraint("fk_cotizaciones_linea_negocio", "cotizaciones", type_="foreignkey")

    op.drop_constraint(
        "uq_margenes_politica_linea_negocio", "margenes_politica", type_="unique"
    )
    op.drop_constraint("margenes_politica_pkey", "margenes_politica", type_="primary")
    op.create_primary_key("margenes_politica_pkey", "margenes_politica", ["linea_negocio"])
    op.drop_column("margenes_politica", "is_active")
    op.drop_column("margenes_politica", "id")

    op.drop_constraint("fk_eventos_conv_sesion", "eventos_conversacion", type_="foreignkey")
    op.drop_constraint("ck_sesiones_estado", "sesiones", type_="check")
    op.add_column("sesiones", sa.Column("contexto", sa.JSON(), nullable=True))
    op.add_column("cotizaciones", sa.Column("payload", sa.JSON(), nullable=True))
    op.create_index("ix_eventos_herr_thread", "eventos_herramienta", ["thread_id"])

    # --- Recrear las 3 vistas originales (0003/0004), ya con las columnas de
    # vuelta en su forma anterior.
    op.execute(f"""
        CREATE VIEW {_V_AJUSTES} AS
        SELECT
            a.comercial_email,
            c.linea_negocio,
            COUNT(*)              AS num_ajustes,
            AVG(a.diferencia)     AS diferencia_promedio,
            MIN(a.diferencia)     AS diferencia_min,
            MAX(a.diferencia)     AS diferencia_max,
            MAX(a.created_at)     AS ultimo_ajuste
        FROM ajustes_margen a
        LEFT JOIN cotizaciones c ON c.id = a.cotizacion_id
        GROUP BY a.comercial_email, c.linea_negocio;
    """)
    op.execute(f"""
        CREATE VIEW {_V_COTIZ} AS
        SELECT
            c.id                       AS cotizacion_id,
            c.folio,
            c.created_at,
            c.estado,
            c.comercial_email,
            c.space_id,
            c.thread_id,
            c.linea_negocio,
            c.cliente_nombre,
            c.cliente_id_sap,
            c.origen,
            c.destino,
            c.costo,
            c.margen_politica,
            c.margen_aplicado,
            c.precio_final,
            c.moneda,
            c.fuente_tarifa,
            c.origen_margen,
            COALESCE(aj.num_ajustes, 0)        AS num_ajustes,
            COALESCE(co.num_correos, 0)        AS num_correos,
            COALESCE(co.num_correos_ok, 0)     AS num_correos_enviados,
            co.ultimo_envio
        FROM cotizaciones c
        LEFT JOIN (
            SELECT cotizacion_id, COUNT(*) AS num_ajustes
            FROM ajustes_margen
            WHERE cotizacion_id IS NOT NULL
            GROUP BY cotizacion_id
        ) aj ON aj.cotizacion_id = c.id
        LEFT JOIN (
            SELECT
                cotizacion_id,
                COUNT(*)                                       AS num_correos,
                COUNT(*) FILTER (WHERE estado = 'enviado')     AS num_correos_ok,
                MAX(created_at)                                AS ultimo_envio
            FROM correos_enviados
            GROUP BY cotizacion_id
        ) co ON co.cotizacion_id = c.id;
    """)
    op.execute(f"""
        CREATE VIEW {_V_ACTIV} AS
        SELECT
            c.comercial_email,
            COUNT(DISTINCT c.id)                                   AS num_cotizaciones,
            COUNT(DISTINCT c.id) FILTER (WHERE c.estado = 'enviada') AS num_cotizaciones_enviadas,
            COALESCE(SUM(sub.num_ajustes), 0)                      AS num_ajustes,
            MAX(c.created_at)                                      AS ultima_cotizacion
        FROM cotizaciones c
        LEFT JOIN (
            SELECT cotizacion_id, COUNT(*) AS num_ajustes
            FROM ajustes_margen
            WHERE cotizacion_id IS NOT NULL
            GROUP BY cotizacion_id
        ) sub ON sub.cotizacion_id = c.id
        WHERE c.comercial_email IS NOT NULL
        GROUP BY c.comercial_email;
    """)
