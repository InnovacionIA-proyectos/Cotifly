# language: es
# HU-003.10 (#16) — Validador de respuestas de IA (equivalente .NET de Instructor, ADR-003 "Salida estructurada").
# Terminado el loop de herramientas, la respuesta final se pide en una llamada aparte, sin herramientas, con
# ChatResponseFormat.ForJsonSchema sobre clsRespuestaAgenteEstructurada {tipo, texto, requiere_confirmacion, acciones_sugeridas}
# y se valida a mano (JSON, campos, tipos, enums, largos, acciones disponibles, montos y folios respaldados por las herramientas
# o el estado). Si no valida: un reintento con un mensaje de corrección; si vuelve a fallar, el error controlado
# (200 {texto, error: true}). Nunca llega al comercial una respuesta sin validar. Por HTTP contra la API real con SQL Server 2019 en
# Testcontainers y el modelo guionizado (sin red).
@integracion
Característica: Validación estructurada de la respuesta final del agente
  Como comercial de Botero Soto
  Quiero que la respuesta del asistente siempre cumpla el formato acordado
  Para no recibir textos rotos ni precios o folios que el servidor no calculó

  Antecedentes:
    Dado la API web de Cotifly con el agente guionizado
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Una respuesta final válida llega al comercial y se pidió con el esquema JSON
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      {"tipo": "conversacional", "texto": "¿De qué ciudad sale la carga?", "requiere_confirmacion": false, "acciones_sugeridas": null}
      """
    Cuando "ana" escribe en el chat "Hola, necesito cotizar"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "¿De qué ciudad sale la carga?"
    Y la llamada final al modelo pidió el esquema JSON "respuesta_agente" sin herramientas

  # HU-003.6 (#84): Gemini vía OmniRoute ignora response_format y envuelve el JSON en prosa y un bloque de código.
  Escenario: Una respuesta final envuelta en prosa y un bloque json llega al comercial
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      Claro, esta es la respuesta final:
      ```json
      {"tipo": "conversacional", "texto": "¿De qué ciudad sale la carga?", "requiere_confirmacion": false, "acciones_sugeridas": null}
      ```
      """
    Cuando "ana" escribe en el chat "Hola, necesito cotizar"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "¿De qué ciudad sale la carga?"

  Escenario: Un JSON incompleto se corrige con un reintento
    Dado que el modelo cierra el loop sin herramientas
    Y que la respuesta final del modelo es:
      """
      {"tipo": "conversacional", "texto": "¿De qué ciudad sale
      """
    Y que la respuesta final del modelo es:
      """
      {"tipo": "conversacional", "texto": "¿De qué ciudad sale la carga?", "requiere_confirmacion": false, "acciones_sugeridas": null}
      """
    Cuando "ana" escribe en el chat "Hola, necesito cotizar"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "¿De qué ciudad sale la carga?"
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
    Cuando "ana" escribe en el chat "Hola, necesito cotizar"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChatError" del contrato
    Y el texto del chat empieza por "No pude estructurar la respuesta"

  Escenario: Un precio que no calcularon las herramientas se rechaza y el reintento usa el del servidor
    Dado que el modelo pide el margen de "Nacional" y calcula el precio con costo 2850000 y margen 0.40 sin cerrar el turno
    Y que la respuesta final del modelo es:
      """
      {"tipo": "previsualizacion", "texto": "El precio final es $3.500.000. ¿Confirmas?", "requiere_confirmacion": true, "acciones_sugeridas": ["confirmar_pdf"]}
      """
    Y que la respuesta final del modelo es:
      """
      {"tipo": "previsualizacion", "texto": "El precio final es $3.352.941. ¿Confirmas?", "requiere_confirmacion": true, "acciones_sugeridas": ["confirmar_pdf", "ajustar_margen"]}
      """
    Cuando "ana" escribe en el chat "Cotiza Nacional YUMBO a BUN, costo 2.850.000"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y el texto del chat es "El precio final es $3.352.941. ¿Confirmas?"
    Y la respuesta del chat trae el margen "0.15" con origen "politica_linea" y el precio "3352941"
    Y el modelo recibió un mensaje de corrección que menciona "$3.500.000"
