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
    Y Nexo recibió la solicitud de "Buenaventura" a "Bogotá, D.C." con el BP "15927" y 2 posiciones

  Escenario: Si Nexo no responde la cotización no se pierde y se reintenta sin volver a llenar el formulario
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "error_envio" con un motivo que menciona "HTTP 503"
    Cuando Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y Nexo recibió la solicitud de "Buenaventura" a "Bogotá, D.C." con el BP "15927" y 2 posiciones

  Escenario: Una cotización que ya llegó a Nexo no se reenvía
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COSTEO_NO_REINTENTABLE"

  # HU-005.6 — Polling del costeo
  Escenario: El polling detecta el costeo de Nexo y deja el cambio de estado en la auditoría
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "autocosteo"
    Y el cambio de estado de "C1" queda en la auditoría
    Cuando Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "entregada"
    Y la cotización "C1" ya no se consulta en Nexo

  Escenario: Con Nexo caído el polling registra el error y sigue consultando
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo deja de responder
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y el error de consulta de "C1" queda en la auditoría
    Cuando Nexo vuelve a responder
    Y Nexo costea la solicitud de "C1" con un flete de 1000000
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "autocosteo"

  Escenario: Una solicitud descartada en Nexo queda descartada y se deja de consultar
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo descarta la solicitud de "C1"
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "descartada"
    Y la cotización "C1" ya no se consulta en Nexo

  Escenario: Una solicitud que nunca se costea queda vencida y visible
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y el polling consulta a Nexo 73 horas después del envío
    Entonces la cotización "C1" queda en el estado de costeo "costeo_vencido" con un motivo que menciona "72 h"

  # HU-005.7 — Margen sobre el costeo y "Mis cotizaciones"
  Escenario: El costeo de Nexo pasa por el margen antes de mostrarse y el costo bruto nunca llega al comercial
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces "Mis cotizaciones" de "ana" muestra "C1" sin precio final
    Cuando Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Entonces "Mis cotizaciones" de "ana" muestra "C1" en "autocosteo" con el precio final 3000000
    Y ninguna respuesta de "Mis cotizaciones" de "ana" contiene el costo 2550000

  # HU-005.14 — Resultado ganada/perdida en Nexo
  Escenario: Marcar ganada escribe primero en Nexo y luego en Cotifly
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "ganada" con el motivo "Mejor precio" y el comentario "Cliente frecuente"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo registró la cotización de "C1" como "Ganada"
    Y la cotización "C1" queda ganada "true" con el motivo "No aplica"

  Escenario: Si Nexo rechaza el resultado no cambia nada en Cotifly
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "Precio alto" y el comentario "-"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COTIZACION_NO_ENTREGADA"
    Y la cotización "C1" sigue sin resultado

  # HU-013.14 (ADR-018 §17) — Consistencia del costeo al entregar y al registrar el resultado
  Escenario: Si Nexo costea en la misma petición el formulario ya responde con el precio
    Dado que Nexo costea la solicitud en la misma petición del alta
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el estado de costeo "autocosteo" con el precio final 3000000

  Escenario: Marcar ganada cuando Nexo ya entregó pero Cotifly no lo ha leído aplica el precio en la misma operación
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y Nexo entrega la cotización de "C1"
    Y "ana" marca el resultado de "C1" como "ganada" con el motivo "Mejor precio" y el comentario "-"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el estado de costeo "entregada" con el precio final 3000000
    Y Nexo registró la cotización de "C1" como "Ganada"
    Y la cotización "C1" ya no se consulta en Nexo
    Y las métricas de "ana" cuentan 1 ganada por un valor de 3000000

  Escenario: Si la relectura falla el resultado se registra y el precio llega con el polling
    Dado que Nexo no responde la consulta del costeo después de registrar el resultado
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y la cotización "C1" está en autocosteo sin el costo leído
    Y Nexo entrega la cotización de "C1"
    Y "ana" marca el resultado de "C1" como "ganada" con el motivo "Mejor precio" y el comentario "-"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el estado de costeo "autocosteo" sin precio final
    Y la cotización "C1" queda ganada "true" con el motivo "No aplica"
    Cuando Nexo vuelve a responder la consulta del costeo
    Y el polling consulta a Nexo
    Entonces "Mis cotizaciones" de "ana" muestra "C1" en "entregada" con el precio final 3000000
    Y la cotización "C1" ya no se consulta en Nexo

  # HU-013.17 (#127) — El cliente desiste antes del costeo: perdida solo en Cotifly
  Escenario: Si el cliente desiste antes del costeo la cotización queda perdida solo en Cotifly
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" marca "C1" como perdida porque "El cliente desistió"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda ganada "false" con el motivo "El cliente desistió"
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y la cotización "C1" ya no se consulta en Nexo
    Y Nexo no recibió ningún resultado de "C1"
    Y las métricas de "ana" cuentan 1 perdida
    Cuando Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda ganada "false" con el motivo "El cliente desistió"
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"

  Escenario: Sin costeo de Nexo no se puede marcar ganada
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" marca el resultado de "C1" como "ganada" con el motivo "Mejor precio" y el comentario "-"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COTIZACION_NO_ENTREGADA"
    Y la cotización "C1" sigue sin resultado
