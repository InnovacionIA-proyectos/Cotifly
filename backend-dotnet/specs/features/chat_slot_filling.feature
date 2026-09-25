# language: es
# HU-003.6 (#12, parte C) — El hallazgo de ADR-004: en el Python el id del cliente no llegaba de forma confiable del chat a
# obtener_margen y generar_pdf (tres nombres para el mismo dato e historial solo de texto entre turnos). En .NET el estado
# estructurado de la conversación (Sesiones.estadoAgente) guarda lo que resuelven las herramientas y el filtro de invocación
# propaga los identificadores y los números: el LLM no los manda. Por HTTP contra la API real con SQL Server 2019 en
# Testcontainers, el modelo guionizado (sin red) y SAP TM con la muestra del Python (export_json).
@integracion
Característica: Estado de la conversación del agente (slot-filling)
  Como comercial de Botero Soto
  Quiero que el cliente y el margen que el agente ya resolvió lleguen solos a la cotización
  Para que el PDF nunca salga con un cliente o un precio que el modelo copió mal

  Antecedentes:
    Dado la API web de Cotifly con el agente guionizado y SAP TM de muestra
    Y el catálogo de clientes tiene a "LONGYIN S.A.S." con NIT "900123456" y BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: El cliente identificado en el turno 1 llega al PDF del turno 2 sin que el LLM lo reenvíe
    Dado que en el turno 1 el modelo busca al cliente "Longyin", pide el margen de "Nacional" para el cliente "Longyin" y calcula el precio con costo 2850000 y margen 0.30
    Y que en el turno 2 el modelo genera el PDF sin cliente ni números
    Cuando "ana" escribe en el chat "Cotiza Nacional Yumbo a Buenaventura para Longyin, costo 2.850.000"
    Entonces la respuesta del chat trae el margen "0.135" con origen "sap_tm" y el precio "3294798"
    Cuando "ana" escribe en el chat "Confirmo"
    Entonces la respuesta del chat trae el PDF con un folio del NIT "900123456"
    Y la cotización del PDF quedó guardada para "ana" con el NIT "900123456" de origen "cliente", la línea "Nacional", el margen "0.135" de origen "sap_tm" y el precio "3294798"

  Escenario: Sin la confirmación explícita del comercial el PDF no se genera
    Dado que en el turno 1 el modelo pide el margen de "Nacional", calcula el precio con costo 2850000 y genera el PDF de una vez
    Cuando "ana" escribe en el chat "Cotiza y genera el PDF de una vez"
    Entonces la herramienta "generar_pdf" respondió al modelo el código "CONFIRMACION_REQUERIDA"
    Y "ana" no tiene cotizaciones guardadas

  Escenario: Sin la búsqueda de clientes (HU-005.2) la cotización queda sin NIT y con el margen de la política
    Dado que la búsqueda de clientes no está disponible
    Y que en el turno 1 el modelo busca al cliente "Falabella", pide el margen de "Nacional" para el cliente "15927" y calcula el precio con costo 2850000 y margen 0.30
    Y que en el turno 2 el modelo genera el PDF sin cliente ni números
    Cuando "ana" escribe en el chat "Cotiza Nacional para Falabella, costo 2.850.000"
    Entonces la respuesta del chat trae el margen "0.15" con origen "politica_linea" y el precio "3352941"
    Cuando "ana" escribe en el chat "Confirmo"
    Entonces la respuesta del chat trae el PDF con un folio sin NIT
    Y la cotización del PDF quedó guardada para "ana" sin NIT, con la línea "Nacional", el margen "0.15" de origen "politica_linea" y el precio "3352941"
