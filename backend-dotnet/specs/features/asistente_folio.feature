# language: es
# HU-013.6 (#102, ADR-018) — Asistente IA por folio: POST /web/chat exige folio y responde {texto, accion_propuesta?}.
# Herramientas de solo lectura o propuesta (consultar_cotizacion, proponer_ajuste_margen, proponer_re_solicitud,
# buscar_servicio_conexo). La IA nunca ejecuta cambios: el frontend confirma la acción propuesta llamando al endpoint
# determinista con el cuerpo exacto y If-Match = row_version. La creación de cotizaciones por chat se retira
# (Chat:CreacionHabilitada=false). Con un IChatClient guionizado, la API real en memoria y SQL Server 2019 en Testcontainers.
# @pendiente @ignore: escenario SDD de HU-013.1 (#97); Reqnroll los genera como omitidos hasta que #102 enlace los pasos y
# quite las dos etiquetas.
@integracion @pendiente @ignore
Característica: Asistente IA sobre una cotización (folio)
  Como comercial de Botero Soto
  Quiero conversar con el asistente sobre una cotización ya creada y confirmar lo que me propone
  Para ajustar la cotización más rápido sin que la IA cambie datos por su cuenta

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró

  Escenario: Sin folio el chat responde FOLIO_REQUERIDO
    Cuando "ana" escribe en el chat "Hola" sin folio
    Entonces la respuesta tiene el estado 400 con el código de negocio "FOLIO_REQUERIDO"
    Y el modelo no fue llamado

  Escenario: Con un folio ajeno responde 404
    Dado el comercial "beto" con sesión en la regional "Occidente"
    Cuando "beto" escribe en el chat "¿Cuál es el precio?" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 404

  Escenario: Una consulta responde solo texto, sin acción propuesta
    Dado que el modelo consulta la cotización y responde "La cotización está costeada en 3.000.000 COP."
    Cuando "ana" escribe en el chat "¿En qué va la cotización?" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y la respuesta del chat no trae acción propuesta
    Y las herramientas del turno son "consultar_cotizacion"

  Escenario: El asistente propone ajustar el margen y no cambia nada
    Dado que el modelo propone ajustar el margen a "0.18"
    Cuando "ana" escribe en el chat "Súbele el margen al 18 %" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y la acción propuesta es "ajustar_margen" con "PUT" al endpoint del margen de "C1" y el cuerpo con el margen "0.18"
    Y el resumen de la acción propuesta menciona "18 %" y el precio 3109756
    Y la acción propuesta trae el row_version actual de "C1"
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"
    Y no hay ajustes de margen de "C1"

  Escenario: Confirmar la acción propuesta ejecuta el endpoint determinista
    Dado que el modelo propone ajustar el margen a "0.18"
    Y "ana" escribió en el chat "Súbele el margen al 18 %" sobre el folio de "C1"
    Cuando "ana" confirma la acción propuesta
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final 3109756 con el margen aplicado "0.18" y el origen "negociado"

  Escenario: Una propuesta por debajo del sugerido trae la justificación para que el comercial la revise
    Dado que el modelo propone ajustar el margen a "0.12" con la justificación "El cliente tiene una oferta más baja"
    Cuando "ana" escribe en el chat "Bájalo al 12 %, tiene otra oferta" sobre el folio de "C1"
    Entonces la acción propuesta es "ajustar_margen" con "PUT" al endpoint del margen de "C1" y el cuerpo con el margen "0.12"
    Y el cuerpo de la acción propuesta trae la justificación "El cliente tiene una oferta más baja"

  Escenario: Confirmar una propuesta vieja responde conflicto de concurrencia
    Dado que el modelo propone ajustar el margen a "0.18"
    Y "ana" escribió en el chat "Súbele el margen al 18 %" sobre el folio de "C1"
    Y "ana" ajusta el margen de "C1" a "0.20" sin justificación
    Cuando "ana" confirma la acción propuesta
    Entonces la respuesta tiene el estado 409 con el código de negocio "CONFLICTO_CONCURRENCIA"
    Y el costeo de "C1" tiene el precio final 3187500 con el margen aplicado "0.20" y el origen "negociado"

  Escenario: El asistente propone agregar un servicio conexo del catálogo
    Dado que el catálogo de Nexo tiene el servicio conexo "Almacenamiento" con el id "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d"
    Y que el modelo busca el servicio conexo "almacenamiento" y propone agregarlo con la cantidad 2
    Cuando "ana" escribe en el chat "Agrégale dos días de almacenamiento" sobre el folio de "C1"
    Entonces la acción propuesta es "agregar_servicios_conexos" con "POST" al endpoint de servicios conexos de "C1"
    Y el cuerpo de la acción propuesta trae en la línea 1 el servicio "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 2
    Y ninguna respuesta del chat contiene el campo "costo_unitario"
    Y Nexo no recibió conexos para la cotización de "C1"

  Escenario: El asistente propone re-solicitar el costeo con cambios de carga
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Y que el modelo propone re-solicitar el costeo con el motivo "El cliente cambió el peso" y el peso 28000
    Cuando "ana" escribe en el chat "El cliente ahora manda 28 toneladas, vuelve a pedir el costo" sobre el folio de "C1"
    Entonces la acción propuesta es "re_solicitar_costeo" con "POST" al endpoint de re-solicitud de "C1"
    Y el cuerpo de la acción propuesta trae el motivo "El cliente cambió el peso" y el peso 28000
    Y Nexo no recibió otra solicitud para "C1"

  Escenario: Si el modelo intenta una herramienta retirada no se ejecuta
    Dado que el modelo pide la herramienta "calcular_precio"
    Cuando "ana" escribe en el chat "Calcula el precio" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200
    Y el error de herramienta del turno es "HERRAMIENTA_DESCONOCIDA"
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"

  Escenario: El chat ya no crea cotizaciones
    Dado que la creación por chat está deshabilitada
    Y que el modelo pide la herramienta "generar_pdf"
    Cuando "ana" escribe en el chat "Cotiza Bogotá a Medellín tractomula" sobre el folio de "C1"
    Entonces "ana" tiene 1 cotización en "Mis cotizaciones"

  Escenario: Las cotizaciones creadas por chat antes del retiro siguen visibles
    Dado que "ana" tiene la cotización de chat "PCN-AN-01092026-001" creada antes de HU-013
    Cuando "ana" consulta "Mis cotizaciones"
    Entonces "Mis cotizaciones" de "ana" incluye "PCN-AN-01092026-001"

  Escenario: Ni la entrada al modelo ni las respuestas del chat contienen montos de dinero
    Dado que Nexo costeó la solicitud de "C1" con el conexo "Cargue" de 85000
    Y que el modelo consulta la cotización y propone ajustar el margen a "0.18"
    Cuando "ana" escribe en el chat "¿Cuánto nos cuesta y cuánto le cobramos?" sobre el folio de "C1"
    Entonces la salida de "consultar_cotizacion" que recibió el modelo trae los márgenes "0.15" y "0.15", el estado, la ruta, la línea, la moneda y el conexo "Cargue"
    Y ningún mensaje enviado al modelo contiene "costo", "2550000", "3000000", "85000" ni "100000"
    Y ninguna respuesta del chat contiene el campo "costo" ni los valores 2550000 ni 85000
    Y los campos precio, margen y pdf de la respuesta del chat son null
    Y el resumen de la acción propuesta lo armó el servidor con el precio 3109756
