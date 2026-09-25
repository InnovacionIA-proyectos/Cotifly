# language: es
# HU-003.6 (#12, parte C) — Asistente de la consola web: POST /web/chat con el contrato de specs/openapi.yaml (MensajeWeb,
# RespuestaChat, RespuestaChatError). La sesión web/{sesion_id} queda ligada al comercial del token; un fallo del agente o del
# proveedor de IA responde 200 con error = true (paridad con el Python). Por HTTP contra la API real con SQL Server 2019 en
# Testcontainers y el modelo guionizado (sin red).
@integracion
Característica: Asistente de cotización en la consola web
  Como comercial de Botero Soto
  Quiero conversar con el agente desde ClientePro
  Para cotizar sin salir del portal y con números que calcula el servidor

  Antecedentes:
    Dado la API web de Cotifly con el agente guionizado
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"

  Escenario: Un turno responde con el precio y el margen que calcularon las herramientas
    Dado que en el turno 1 el modelo pide el margen de "Nacional" y calcula el precio con costo 2850000 y margen 0.40
    Cuando "ana" escribe en el chat "Cotiza Nacional YUMBO a BUN, costo 2.850.000"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChat" del contrato
    Y la respuesta del chat trae el margen "0.15" con origen "politica_linea" y el precio "3352941"
    Y la respuesta del chat lista las herramientas "obtener_margen, calcular_precio"

  Escenario: La sesión de otro comercial no se continúa
    Dado que el modelo responde "Hola Ana."
    Cuando "ana" escribe en el chat "hola" en la sesión "sesion-compartida"
    Y "luis" escribe en el chat "¿qué cotizó Ana?" en la sesión "sesion-compartida"
    Entonces la respuesta tiene el estado 404 con el error "Sesión no encontrada."

  Escenario: El límite de peticiones del proveedor de IA responde el error controlado
    Dado que el proveedor de IA responde con el límite de peticiones
    Cuando "ana" escribe en el chat "hola"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "RespuestaChatError" del contrato
    Y el texto del chat empieza por "⏳ El modelo alcanzó su límite de peticiones por minuto."

  Escenario: Un sesion_id inválido se rechaza
    Cuando "ana" escribe en el chat "hola" en la sesión "no válida"
    Entonces la respuesta tiene el estado 400 con el error "sesion_id inválido"
