# language: es
# HU-005.5 a HU-005.14 (ADR-015) — Costeo en Nexo desde la consola del comercial: envío del formulario ampliado, polling del
# costeo, margen sobre el costeo y resultado ganada/perdida. Por HTTP contra la API real en memoria, SQL Server 2019 en
# Testcontainers y un Nexo falso (nunca el real).
@integracion
Característica: Costeo de la cotización en Nexo
  Como comercial de Botero Soto
  Quiero enviar la solicitud de costeo a Nexo desde Cotifly y seguir su estado
  Para cotizarle al cliente con el costo real de Nexo sin salir de ClientePro

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: La solicitud llega a Nexo y la cotización queda pendiente de costeo
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y Nexo recibió la solicitud de "Buenaventura" a "Bogotá D.C." con el BP "15927" y 2 posiciones

  Escenario: Si Nexo no responde la cotización no se pierde y se reintenta sin volver a llenar el formulario
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "error_envio" con un motivo que menciona "HTTP 503"
    Cuando Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y Nexo recibió la solicitud de "Buenaventura" a "Bogotá D.C." con el BP "15927" y 2 posiciones

  Escenario: Una cotización que ya llegó a Nexo no se reenvía
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COSTEO_NO_REINTENTABLE"
