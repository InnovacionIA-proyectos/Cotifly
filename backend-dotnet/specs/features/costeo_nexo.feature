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
  Escenario: El costeo de Nexo pasa por el margen antes de mostrarse y el costo bruto no sale en Mis cotizaciones
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

  # HU-033 (#199, ADR-023, decisión 6) — Resultado diferido: si Nexo todavía no tiene la cotización Entregada, el resultado queda solo en
  # Cotifly ("pendiente de sincronizar") y el polling lo envía a Nexo una sola vez cuando la Mesa la entrega. Cotifly no llama a /entregar.
  Escenario: Si Nexo todavía no la entregó el resultado queda pendiente y se envía una sola vez cuando la Mesa la entrega
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "Precio alto" y el comentario "Pidió 5 % menos"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae la sincronización del resultado "pendiente"
    Y la cotización "C1" queda ganada "false" con el motivo "Precio alto"
    Y Nexo no recibió ningún resultado de "C1"
    Cuando el polling consulta a Nexo
    Entonces Nexo no recibió ningún resultado de "C1"
    Cuando Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Entonces Nexo registró la cotización de "C1" como "Perdida"
    Y el resultado de "C1" queda "sincronizado" con Nexo
    # Dos envíos a Nexo en total: el del comercial (409 COTIZACION_NO_ENTREGADA) y el único del polling tras la entrega.
    Cuando el polling consulta a Nexo
    Entonces Nexo recibió 2 envíos del resultado de "C1"

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

  # HU-033 (#199): antes era COTIZACION_NO_ENTREGADA; la ganada sin costeo queda pendiente de sincronizar (la perdida sigue siendo #127).
  Escenario: Sin costeo de Nexo la ganada queda pendiente de sincronizar con Nexo
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" marca el resultado de "C1" como "ganada" con el motivo "Mejor precio" y el comentario "-"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae la sincronización del resultado "pendiente"
    Y la cotización "C1" queda ganada "true" con el motivo "No aplica"
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y Nexo no recibió ningún resultado de "C1"

  # HU-018.1 (#155) — "Ver detalle": la fecha límite (cabecera) y el scorePrioridad que Nexo da al crear la solicitud
  Escenario: Ver detalle muestra la fecha límite y el score de prioridad que Nexo dio al crear la solicitud
    Dado que Nexo responde el alta con el score de prioridad "62.5"
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" con la fecha límite "2026-10-20T17:00:00-05:00" como "C1"
    Y "ana" abre el costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y la respuesta trae la fecha límite "2026-10-20T22:00:00+00:00" y el score de prioridad "62.5"
    Y la cotización "C1" guarda el score de prioridad "62.5"

  Escenario: Si Nexo no manda el score de prioridad el detalle lo muestra vacío
    Dado que Nexo responde el alta sin score de prioridad
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" abre el costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y la respuesta trae la fecha límite vacía y el score de prioridad vacío

  # #181 (Nexo #309) — Por qué la línea del camión sigue pendiente en Nexo: se muestra en el costeo, sin guardarlo en la base
  Escenario: El costeo muestra por qué la línea del camión sigue pendiente en Nexo
    Dado que Nexo expone el estado y el motivo pendiente de cada línea
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo deja pendiente la línea de "C1" con el motivo "Línea sin vehículo: requiere consulta completa"
    Y el polling consulta a Nexo
    Y "ana" abre el costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el camión de "C1" está "por_costear" en Nexo con el motivo "Línea sin vehículo: requiere consulta completa"
    Cuando Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Y "ana" abre el costeo de "C1"
    Entonces el camión de "C1" está "auto_resuelta" en Nexo sin motivo pendiente

  Escenario: Con un Nexo que no expone el estado de la línea el costeo sale sin motivo y sin error
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y el polling consulta a Nexo
    Y "ana" abre el costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el camión de "C1" sale sin estado de línea de Nexo ni motivo pendiente
