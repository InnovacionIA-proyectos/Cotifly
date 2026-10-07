# language: es
# HU-003.10 (#16) — Validador de respuestas de IA (equivalente .NET de Instructor, ADR-003 "Salida estructurada").
# Terminado el loop de herramientas, la respuesta final se pide en una llamada aparte, sin herramientas, con
# ChatResponseFormat.ForJsonSchema sobre clsRespuestaAgenteEstructurada {tipo, texto, requiere_confirmacion, acciones_sugeridas}
# y se valida a mano (JSON, campos, tipos, enums, largos, acciones disponibles, montos y folios respaldados por las herramientas
# o el estado). Si no valida: un reintento con un mensaje de corrección; si vuelve a fallar, el error controlado
# (200 {texto, error: true}). Nunca llega al comercial una respuesta sin validar. Por HTTP contra la API real con SQL Server 2019 en
# Testcontainers y el modelo guionizado (sin red). Desde HU-013.9 (#105) el único agente es el asistente por folio (HU-013.6): los
# escenarios corren sobre una cotización costeada; el del precio calculado por las herramientas se retiró con el agente legado (el
# asistente no calcula precios ni ve montos, ver asistente_folio.feature).
@integracion
Característica: Validación estructurada de la respuesta final del agente
  Como comercial de Botero Soto
  Quiero que la respuesta del asistente siempre cumpla el formato acordado
  Para no recibir textos rotos ni precios o folios que el servidor no calculó

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró

  Escenario: Una respuesta final válida llega al comercial y se pidió con el esquema JSON
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      {"tipo": "conversacional", "texto": "La cotización está costeada y lista para generar el PDF.", "requiere_confirmacion": false, "acciones_sugeridas": null}
      """
    Cuando "ana" escribe en el chat "¿En qué va la cotización?" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "La cotización está costeada y lista para generar el PDF."
    Y la llamada final al modelo pidió el esquema JSON "respuesta_agente" sin herramientas

  # HU-003.6 (#84): Gemini vía OmniRoute ignora response_format y envuelve el JSON en prosa y un bloque de código.
  Escenario: Una respuesta final envuelta en prosa y un bloque json llega al comercial
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      Claro, esta es la respuesta final:
      ```json
      {"tipo": "conversacional", "texto": "La cotización está costeada y lista para generar el PDF.", "requiere_confirmacion": false, "acciones_sugeridas": null}
      ```
      """
    Cuando "ana" escribe en el chat "¿En qué va la cotización?" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "La cotización está costeada y lista para generar el PDF."

  Escenario: Un JSON incompleto se corrige con un reintento
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      {"tipo": "conversacional", "texto": "La cotización está
      """
    Y que la respuesta final del modelo es:
      """
      {"tipo": "conversacional", "texto": "La cotización está costeada y lista para generar el PDF.", "requiere_confirmacion": false, "acciones_sugeridas": null}
      """
    Cuando "ana" escribe en el chat "¿En qué va la cotización?" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "La cotización está costeada y lista para generar el PDF."
    Y el modelo recibió un mensaje de corrección que menciona "JSON"

  Escenario: Una acción inventada dos veces termina en el error controlado
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      {"tipo": "confirmacion_requerida", "texto": "¿Reservo el vehículo?", "requiere_confirmacion": true, "acciones_sugeridas": ["reservar_vehiculo"]}
      """
    Y que la respuesta final del modelo es:
      """
      {"tipo": "confirmacion_requerida", "texto": "¿Reservo el vehículo?", "requiere_confirmacion": true, "acciones_sugeridas": ["reservar_vehiculo"]}
      """
    Cuando "ana" escribe en el chat "¿En qué va la cotización?" sobre el folio de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChatError" del contrato
    Y el texto del chat empieza por "No pude estructurar la respuesta"
