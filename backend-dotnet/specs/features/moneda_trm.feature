# language: es
# HU-012 (#50, #51, #52) — Cotización en dólares con la TRM del día (ADR-014).
# Fuentes de la TRM (Trm:Proveedor): por defecto DolarApi (co.dolarapi.com/v1/trm) con respaldo en los datos abiertos de la
# Superintendencia Financiera (conjunto 32sa-8pi3). Aquí las atienden servidores falsos sin red (tests/Comun/clsDolarApiFalso.cs y
# tests/Comun/clsTrmFalso.cs). La Superintendencia falsa tiene la TRM real de la semana del 2026-09-21: la del viernes 25 (3306,86)
# vale del sábado 26 al lunes 28. Los escenarios que no nombran el proveedor usan solo la Superintendencia ("superfinanciera"); los de
# DolarApi lo eligen con "que la TRM se consulta con el proveedor". Las fechas son de America/Bogota. Registro real de dependencias
# (AddAplicacion + AddInfraestructura) sobre SQL Server 2019 en Testcontainers. Las características corren en paralelo sobre la
# misma base: este archivo usa su propio comercial ("trm.comercial"), nunca el de pdf.feature.
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

  # HU-012.1 (requisito de negocio del 2026-10-05, ADR-014 §1): DolarApi como fuente principal y la Superintendencia como respaldo.
  Escenario: DolarApi responde la TRM del día
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y que la fecha de negocio es "2026-10-05"
    Y que DolarApi publica la TRM "3273.49" el "2026-10-05T13:01:18.476Z"
    Cuando consulto la TRM del día
    Entonces la TRM es "3273.49" de la fuente "dolarapi"
    Y la fuente de la TRM recibió 0 consultas

  # Verificado el 2026-10-05: fechaActualizacion es la hora en que DolarApi refrescó el dato, no la vigencia. Pasadas las 12:00 en Bogotá
  # el valor puede ser ya la TRM de mañana: se usa el respaldo con la vigencia exacta del día pedido.
  Escenario: DolarApi refrescado en la tarde no se usa: puede traer la TRM de mañana
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y que la fecha de negocio es "2026-09-29"
    Y que DolarApi publica la TRM "3320.00" el "2026-09-29T20:00:00.000Z"
    Cuando consulto la TRM del día
    Entonces la TRM es "3312.50" de la fuente "superfinanciera"

  Escenario: La primera TRM del día queda fija hasta el fin del día
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y que la fecha de negocio es "2026-10-05"
    Y que DolarApi publica la TRM "3273.49" el "2026-10-05T13:01:18.476Z"
    Cuando consulto la TRM del día
    Y pasan 9 horas y DolarApi publica la TRM "3300.00" el "2026-10-05T22:00:00.000Z"
    Y consulto la TRM del día
    Entonces la TRM es "3273.49" de la fuente "dolarapi"

  Escenario: DolarApi caído, la TRM sale del respaldo
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y que la fecha de negocio es "2026-09-29"
    Y que DolarApi no responde
    Cuando consulto la TRM del día
    Entonces la TRM es "3312.50" de la fuente "superfinanciera"
    Y la fuente de la TRM recibió 1 consulta

  Escenario: DolarApi con una TRM desactualizada, la TRM sale del respaldo
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y que la fecha de negocio es "2026-09-29"
    Y que DolarApi publica la TRM "3273.49" el "2026-09-20T19:01:18.476Z"
    Cuando consulto la TRM del día
    Entonces la TRM es "3312.50" de la fuente "superfinanciera"

  Escenario: DolarApi y el respaldo caídos
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y que la fecha de negocio es "2026-09-29"
    Y que DolarApi no responde
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
    Dado el comercial "trm.comercial" con nombre "Tatiana Restrepo Mejía" y correo "trm.comercial@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-27"
    Cuando "trm.comercial" cotiza en "USD" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-USD-27092026"
    Entonces la cotización "PCN-USD-27092026" queda guardada en "USD" con precio final "996.35" y TRM "3306.86"

  Escenario: En COP la TRM guardada es 0, no nula
    Dado el comercial "trm.comercial" con nombre "Tatiana Restrepo Mejía" y correo "trm.comercial@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-27"
    Cuando "trm.comercial" cotiza en "COP" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-COP-27092026"
    Entonces la cotización "PCN-COP-27092026" queda guardada en "COP" con precio final "3294798" y TRM "0"

  # HU-012.3 (#52): PDF en dólares con la nota de TRM mínima (texto de negocio del 2026-10-05, extraído con PdfPig). La TRM es la
  # aplicada en la cotización, con 2 decimales.
  Escenario: PDF en dólares con la nota de TRM mínima
    Dado el comercial "trm.comercial" con nombre "Tatiana Restrepo Mejía" y correo "trm.comercial@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-28"
    Cuando "trm.comercial" cotiza en "USD" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-USD-28092026"
    Entonces la cotización "PCN-USD-28092026" queda guardada en "USD" con precio final "996.35" y TRM "3306.86"
    Y el PDF contiene "US$996,35"
    Y el PDF no contiene "Cotización en dólares"
    Y el PDF contiene "Esta cotización está bajo una TRM mínima de $3.306,86 y es de resaltar que la tarifa puede variar de acuerdo al comportamiento de la misma a la fecha de elaboración de la CPI."
    Y el PDF no contiene "mínimo a pagar"
    Y el PDF no contiene "se ajusta día a día"

  Escenario: PDF en dólares con la TRM de DolarApi
    Dado que la TRM se consulta con el proveedor "dolarapi_con_respaldo"
    Y el comercial "trm.comercial" con nombre "Tatiana Restrepo Mejía" y correo "trm.comercial@boterosoto.com.co"
    Y que la fecha de negocio es "2026-10-05"
    Y que DolarApi publica la TRM "3273.49" el "2026-10-05T13:01:18.476Z"
    Cuando "trm.comercial" cotiza en "USD" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-USD-05102026"
    Entonces la cotización "PCN-USD-05102026" queda guardada en "USD" con precio final "1006.51" y TRM "3273.49"
    Y el PDF contiene "US$1.006,51"
    Y el PDF contiene "Esta cotización está bajo una TRM mínima de $3.273,49 y es de resaltar que la tarifa puede variar de acuerdo al comportamiento de la misma a la fecha de elaboración de la CPI."

  Escenario: El PDF en pesos no trae la nota de TRM
    Dado el comercial "trm.comercial" con nombre "Tatiana Restrepo Mejía" y correo "trm.comercial@boterosoto.com.co"
    Y que la fecha de negocio es "2026-09-28"
    Cuando "trm.comercial" cotiza en "COP" la solicitud de ejemplo con un costo de 2850000 y margen "0.135" con el folio "PCN-COP-28092026"
    Entonces la cotización "PCN-COP-28092026" queda guardada en "COP" con precio final "3294798" y TRM "0"
    Y el PDF contiene "$3.294.798"
    Y el PDF no contiene "TRM"
    Y el PDF no contiene "US$"
    Y el PDF no contiene "elaboración de la CPI"

  # Licitaciones en USD (ADR-014 §5): la TRM del día de creación queda fija en la licitación; el polling calcula cada precio en pesos
  # y lo convierte con ella. Por HTTP contra la API real en memoria con el Nexo falso y DolarApi falso (publica "hoy").
  # Nacional al 15 %: flete 1.000.000 → 1.176.471 pesos → / 3.273,49 = US$359,39.
  Escenario: Licitación en USD con la TRM de DolarApi y la nota en el PDF del pliego
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y que DolarApi publica hoy la TRM "3273.49"
    Cuando "ana" crea la licitación "LUSD" en "USD" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la respuesta tiene el estado 201 y cumple el esquema "Licitacion" del contrato
    Y la licitación "LUSD" está en "USD" con la TRM "3273.49"
    Cuando Nexo costea las líneas 1 y 2 de "LUSD" con un flete de 1000000
    Y Nexo costea las líneas 3 de "LUSD" con un flete de 1000000
    Y el polling consulta a Nexo por "LUSD"
    Entonces la licitación "LUSD" queda en el estado de costeo "autocosteo" con 3 líneas con precio
    Y el precio final de "LUSD" es la suma de sus líneas
    Y cada línea con precio de "LUSD" vale "359.39"
    Cuando "ana" genera el PDF de la licitación "LUSD"
    Entonces el PDF del pliego contiene "US$359,39"
    Y el PDF del pliego contiene "Esta cotización está bajo una TRM mínima de $3.273,49 y es de resaltar que la tarifa puede variar de acuerdo al comportamiento de la misma a la fecha de elaboración de la CPI."

  Escenario: Una licitación en USD sin TRM no se crea ni llega a Nexo
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y que las fuentes de la TRM no responden
    Cuando "ana" crea la licitación "LUSD" en "USD" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la respuesta tiene el estado 400 con el código de negocio "TRM_NO_DISPONIBLE"
    Y Nexo no recibió ninguna solicitud
