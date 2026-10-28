# language: es
# HU-016.1 (#154) — Listado regional del gerente comercial: GET /web/cotizaciones/regional. HU-037.3 (#211): el director ve todas
# las regionales o la que elige con ?regional=, y GET /web/regionales da las del selector. Por HTTP contra la API real en
# memoria, con sesiones del login SSO (el rol sale del claim `rol` del token) y SQL Server 2019 en Testcontainers. La regional se
# fija en UsuariosWeb.regional (DimEjecutivo no está configurada en estas pruebas). Los nombres de comerciales, regionales y
# cotizaciones son alias: cada escenario crea los suyos.
@integracion
Característica: Listado de las cotizaciones de la regional del gerente comercial
  Como gerente comercial de una regional
  Quiero ver fila por fila las cotizaciones de los comerciales de mi regional
  Para hacer seguimiento a su trabajo sin ver las de otras regionales

  Antecedentes:
    Dado la API web de Cotifly
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"
    Y el comercial "marta" con sesión en la regional "Norte"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y el gerente comercial "hugo" con sesión en la regional "Norte"
    Y que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Y que "luis" tiene la cotización web "L1" del cliente "800333444" con su PDF
    Y que "marta" tiene la cotización web "M1" del cliente "800555666" con su PDF
    Y que "gloria" tiene la cotización web "G1" del cliente "900777888" con su PDF

  Escenario: El gerente ve las cotizaciones de su regional, de la más reciente a la más antigua, con su comercial
    Cuando "gloria" consulta las cotizaciones de su regional
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionesRegional" del contrato
    Y el listado regional trae en orden las cotizaciones "G1", "L1" y "A1"
    Y la cotización "L1" del listado regional es de "luis"
    Y la cotización "A1" del listado regional es de "ana"
    Y el listado no incluye la cotización "M1"

  Escenario: Un gerente de otra regional no ve las cotizaciones de esta
    Cuando "hugo" consulta las cotizaciones de su regional
    Entonces la respuesta tiene el estado 200
    Y el listado incluye la cotización "M1"
    Y el listado no incluye la cotización "A1"
    Y el listado no incluye la cotización "L1"
    Y el listado no incluye la cotización "G1"

  # HU-037.3 (#211): el director ve todas las regionales y puede filtrar por una; el gerente solo la suya.
  Escenario: El director ve las cotizaciones de todas las regionales
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta las cotizaciones de su regional
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionesRegional" del contrato
    Y el listado regional es de todas las regionales

  Escenario: El director filtra el listado por una regional
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta las cotizaciones de la regional "Norte"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionesRegional" del contrato
    Y el listado regional es de la regional "Norte"
    Y el listado incluye la cotización "M1"
    Y el listado no incluye la cotización "A1"
    Y el listado no incluye la cotización "G1"

  Escenario: El gerente puede pedir su propia regional pero no otra
    Cuando "gloria" consulta las cotizaciones de la regional "Occidente"
    Entonces la respuesta tiene el estado 200
    Y el listado incluye la cotización "A1"
    Y el listado no incluye la cotización "M1"
    Cuando "gloria" consulta las cotizaciones de la regional "Norte"
    Entonces la respuesta tiene el estado 403 con el código de negocio "REGIONAL_NO_PERMITIDA"

  Escenario: Las regionales del selector: todas para el director, la suya para el gerente y ninguna para el comercial
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta las regionales disponibles
    Entonces la respuesta tiene el estado 200 y cumple el esquema "Regionales" del contrato
    Y las regionales disponibles incluyen "Occidente", "Norte" y "Sur"
    Cuando "gloria" consulta las regionales disponibles
    Entonces la respuesta tiene el estado 200
    Y las regionales disponibles son solo "Occidente"
    Cuando "ana" consulta las regionales disponibles
    Entonces la respuesta tiene el estado 403 con el error "No tienes permiso para esta operación."

  Escenario: Un comercial no puede ver el listado de la regional
    Cuando "ana" consulta las cotizaciones de su regional
    Entonces la respuesta tiene el estado 403 con el error "No tienes permiso para esta operación."

  Escenario: Un gerente sin regional asignada recibe un error claro
    Dado el gerente comercial "ines" con sesión y sin regional
    Cuando "ines" consulta las cotizaciones de su regional
    Entonces la respuesta tiene el estado 409 con el código de negocio "REGIONAL_NO_ASIGNADA"
