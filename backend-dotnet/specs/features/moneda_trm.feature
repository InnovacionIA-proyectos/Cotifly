# language: es
# HU-012 (#50, #51, #52) — Cotización en dólares con la TRM oficial del día (ADR-013).
# La fuente de la TRM son los datos abiertos de la Superintendencia Financiera (conjunto 32sa-8pi3); aquí la atiende un
# servidor falso sin red (tests/Comun/clsTrmFalso.cs) con la TRM real de la semana del 2026-09-21: la del viernes 25
# (3306,86) vale del sábado 26 al lunes 28. Las fechas son de America/Bogota. Registro real de dependencias
# (AddAplicacion + AddInfraestructura) sobre SQL Server 2019 en Testcontainers.
@integracion @trm
Característica: Cotización en dólares con la TRM del día
  Como comercial de Botero Soto
  Quiero cotizar en dólares con la TRM oficial del día
  Para atender a los clientes que necesitan la cotización en USD sin convertir a mano

  # HU-012.1 (#50): fuente oficial, rangos de vigencia y caché por día.
  Escenario: Un fin de semana vale la TRM del rango que lo cubre
    Dado que la fecha de negocio es "2026-09-27"
    Cuando consulto la TRM del día
    Entonces la TRM es "3306.86" vigente del "2026-09-26" al "2026-09-28"

  Escenario: La TRM se consulta una sola vez por día
    Dado que la fecha de negocio es "2026-09-29"
    Cuando consulto la TRM del día 3 veces
    Entonces la TRM es "3312.50" vigente del "2026-09-29" al "2026-09-29"
    Y la fuente de la TRM recibió 1 consulta

  Escenario: Fuente de TRM no disponible
    Dado que la fecha de negocio es "2026-09-29"
    Y que la fuente de la TRM no responde
    Cuando consulto la TRM del día
    Entonces la consulta falla con el código "TRM_NO_DISPONIBLE"
