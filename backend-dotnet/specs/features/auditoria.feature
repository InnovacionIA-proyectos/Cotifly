# language: es
# HU-003.5 (#11) — Auditoría transversal (port de mcp_server/auditoria.py).
# El Python auditaba cada invocación de herramienta del MCP en EventosHerramienta. En .NET los casos de uso
# de generar PDF, enviar correo y registrar el ajuste de margen se auditan solos (también cuando los llama
# un endpoint web, no solo el agente); el filtro del agente de #12 audita el resto de herramientas con el
# mismo servicio. La auditoría escribe en su propio ámbito y nunca interrumpe el caso de uso.
@integracion
Característica: Auditoría de las acciones de negocio
  Como responsable de Cotifly
  Quiero que cada generación de PDF, envío de correo y ajuste de margen quede auditado
  Para tener trazabilidad sin depender del modelo de IA

  Antecedentes:
    Dado el comercial "sara.auditoria" con nombre "Sara Auditoria" y correo "sara.auditoria@boterosoto.com.co"

  Escenario: Generar un PDF queda auditado como exitoso en su hilo
    Cuando "sara.auditoria" genera el PDF de la solicitud de ejemplo con el folio "PCN-01112026" en el hilo "web/aud-1"
    Entonces el hilo "web/aud-1" tiene un evento "generar_pdf" exitoso
    Y el evento "generar_pdf" del hilo "web/aud-1" tiene el folio "PCN-01112026" en su resultado

  Escenario: Un rechazo de negocio queda auditado como fallo con su código
    Cuando "sara.auditoria" genera el PDF de la línea "Marítimo" con el folio "PCN-02112026" en el hilo "web/aud-2"
    Entonces el hilo "web/aud-2" tiene un evento "generar_pdf" fallido
    Y el evento "generar_pdf" del hilo "web/aud-2" tiene el código "MARGEN_LINEA_DESCONOCIDA" en su resultado

  Escenario: Un envío de correo queda auditado
    Dado el envío de correo deshabilitado
    Y que "sara.auditoria" tiene la cotización "PCN-03112026" del cliente "900123456" con su PDF
    Cuando "sara.auditoria" envía la cotización "PCN-03112026" a "cliente@empresa.com" en el hilo "web/aud-3"
    Entonces el hilo "web/aud-3" tiene un evento "enviar_correo" fallido

  Escenario: Un ajuste de margen negociado queda auditado
    Cuando "sara.auditoria" negocia el margen "0.12" sobre la política "0.15" en el hilo "web/aud-4"
    Entonces el hilo "web/aud-4" tiene un evento "registrar_ajuste_margen" exitoso
