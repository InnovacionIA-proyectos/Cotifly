# language: es
# HU-003.6 (#12, parte B) — Consola web del comercial: endpoints /web/* (salvo /web/chat, parte C) con el contrato de
# specs/openapi.yaml: JSON en snake_case, errores ErrorWeb / ErrorNegocio y los códigos HTTP de la tabla "Para #12".
# Por HTTP contra la API real en memoria con SQL Server 2019 en Testcontainers.
@integracion
Característica: Consola web del comercial
  Como comercial de Botero Soto
  Quiero consultar y gestionar mis cotizaciones desde ClientePro
  Para dar seguimiento a mis clientes sin salir del portal

  Antecedentes:
    Dado la API web de Cotifly
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Mis cotizaciones llegan en snake_case, con la línea de negocio y de la más reciente a la más antigua
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Y que "ana" tiene la cotización web "A2" del cliente "900111222" con su PDF
    Cuando "ana" consulta sus cotizaciones por la API
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MisCotizaciones" del contrato
    Y el listado empieza por la cotización "A2" seguida de "A1"
    Y la cotización "A1" del listado trae la línea "Nacional", el estado de envío "sin_enviar" y su pdf_url

  Escenario: Marcar una cotización como perdida devuelve su resumen actualizado
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Cuando "ana" marca la cotización "A1" como ganada "false" con el motivo "Valor muy alto"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae ganada "false" y motivo_rechazo "Valor muy alto"

  # HU-021.13 (propuesta de refactor de BD §2.1): ganada no deja motivo_rechazo en null, lo deja en "No aplica".
  Escenario: Marcar una cotización como ganada deja el motivo de rechazo en "No aplica"
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Cuando "ana" marca la cotización "A1" como ganada "true" con el motivo "Cualquier texto"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae ganada "true" y motivo_rechazo "No aplica"

  Escenario: Un motivo de rechazo de más de 2000 caracteres se rechaza
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Cuando "ana" marca la cotización "A1" como ganada "false" con un motivo de 2001 caracteres
    Entonces la respuesta tiene el estado 400 con un ErrorWeb

  Escenario: El teléfono del comercial se guarda, se consulta y se valida
    Cuando "ana" guarda el teléfono "+57 300 123 4567"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TelefonoComercial" del contrato
    Cuando "ana" consulta su teléfono
    Entonces la respuesta trae el teléfono "+57 300 123 4567"
    Cuando "ana" guarda el teléfono "llámame"
    Entonces la respuesta tiene el estado 400 con un ErrorWeb

  Escenario: Los datos para una versión nueva traen el folio base y la versión siguiente
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Cuando "ana" pide los datos para una versión nueva de "A1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "DatosNuevaVersion" del contrato
    Y los datos traen el folio base de "A1" y la versión siguiente 2

  Escenario: La vista previa del correo usa el asunto por defecto y el PDF de la cotización
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Cuando "ana" previsualiza el correo de "A1" sin borrador
    Entonces la respuesta tiene el estado 200 y cumple el esquema "VistaPreviaCorreo" del contrato
    Y la vista previa tiene el asunto por defecto de "A1" y adjunta su PDF

  Escenario: Con el correo deshabilitado el envío responde 503 con ErrorNegocio
    Dado que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Cuando "ana" envía por la API la cotización "A1" a "cliente@empresa.com"
    Entonces la respuesta tiene el estado 503 con el código de negocio "CORREO_DESHABILITADO"

  Escenario: Un NIT de más de 20 caracteres en destinatarios previos se rechaza
    Cuando "ana" consulta por la API los destinatarios previos del cliente "123456789012345678901"
    Entonces la respuesta tiene el estado 400 con un ErrorWeb

  Escenario: Sin sesión la API web responde 401
    Cuando se consulta "GET /web/mis-cotizaciones" sin sesión
    Entonces la respuesta tiene el estado 401 con el error "Inicia sesión de nuevo."
