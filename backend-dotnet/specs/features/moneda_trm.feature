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

  # HU-012.2 (#51): el precio se calcula en pesos y, en USD, se convierte con la TRM del día (2 decimales, half-to-even).
  Escenario: Comercial elige cotizar en USD
    Dado que la fecha de negocio es "2026-09-27"
    Cuando calculo en "USD" el precio de un costo de 2850000 con margen "0.135" por el método "margen"
    Entonces el precio es "996.35" en "USD" con la TRM "3306.86"
    Y el precio en pesos es 3294798 y la utilidad en pesos 444798

  Escenario: Cotización en COP no usa TRM
    Dado que la fecha de negocio es "2026-09-27"
    Cuando calculo en "COP" el precio de un costo de 2850000 con margen "0.135" por el método "margen"
    Entonces el precio es "3294798" en "COP" con la TRM "0"
    Y la fuente de la TRM recibió 0 consultas

  Escenario: Fuente de TRM no disponible al cotizar en USD
    Dado que la fecha de negocio es "2026-09-29"
    Y que la fuente de la TRM no responde
    Cuando calculo en "USD" el precio de un costo de 2850000 con margen "0.135" por el método "margen"
    Entonces el cálculo en la moneda pedida se rechaza con el código "TRM_NO_DISPONIBLE"

  Escenario: La TRM aplicada queda guardada en la cotización en USD
    Dado el comercial "nathaly.vargas" con nombre "Nathaly Vargas Garcia" y correo "nvargas@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-27"
    Cuando "nathaly.vargas" cotiza en "USD" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-USD-27092026"
    Entonces la cotización "PCN-USD-27092026" queda guardada en "USD" con precio final "996.35" y TRM "3306.86"

  Escenario: En COP la TRM guardada es 0, no nula
    Dado el comercial "nathaly.vargas" con nombre "Nathaly Vargas Garcia" y correo "nvargas@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-27"
    Cuando "nathaly.vargas" cotiza en "COP" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-COP-27092026"
    Entonces la cotización "PCN-COP-27092026" queda guardada en "COP" con precio final "3294798" y TRM "0"

  # HU-012.3 (#52): PDF en dólares con la aclaración del piso mínimo (texto extraído con PdfPig).
  Escenario: PDF en dólares con aclaración del piso
    Dado el comercial "nathaly.vargas" con nombre "Nathaly Vargas Garcia" y correo "nvargas@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-28"
    Cuando "nathaly.vargas" cotiza en "USD" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-USD-28092026"
    Entonces la cotización "PCN-USD-28092026" queda guardada en "USD" con precio final "996.35" y TRM "3306.86"
    Y el PDF contiene "US$996,35"
    Y el PDF contiene "Cotización en dólares:"
    Y el PDF contiene "TRM de $3.306,86 por dólar"
    Y el PDF contiene "se ajusta día a día con la TRM vigente"
    Y el PDF contiene "el mínimo a pagar es el valor cotizado originalmente"

  Escenario: El PDF en pesos no trae ninguna aclaración de TRM
    Dado el comercial "nathaly.vargas" con nombre "Nathaly Vargas Garcia" y correo "nvargas@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-28"
    Cuando "nathaly.vargas" cotiza en "COP" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-COP-28092026"
    Entonces la cotización "PCN-COP-28092026" queda guardada en "COP" con precio final "3294798" y TRM "0"
    Y el PDF contiene "$3.294.798"
    Y el PDF no contiene "TRM"
    Y el PDF no contiene "US$"
