# language: es
# HU-013.4 (#100, ADR-018) — "Cotizar costo": POST /web/cotizaciones/{folio}/costeo/re-solicitar con {motivo, datos_carga?}.
# Sin cambios de carga se reenvía la misma cotización (mismo folio); con cambios de carga se crea la versión {folio}-vN y la
# anterior se conserva. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un Nexo falso.
# @pendiente @ignore: escenario SDD de HU-013.1 (#97); Reqnroll los genera como omitidos hasta que #100 enlace los pasos y
# quite las dos etiquetas.
@integracion @pendiente @ignore
Característica: Re-solicitar el costeo a Nexo
  Como comercial de Botero Soto
  Quiero volver a pedir el costo a Nexo cuando el costeo venció, ya se entregó o falló el envío
  Para cotizar con un costo vigente, también cuando el cliente cambia la carga

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"

  Escenario: Un costeo vencido se re-solicita con el mismo folio y se reanuda el polling
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "El costeo venció"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y la cotización "C1" conserva su folio y su versión 1
    Y Nexo recibió una segunda solicitud de "Buenaventura" a "Bogotá D.C." con el BP "15927" y 2 posiciones
    Y la re-solicitud de "C1" queda en la auditoría con el motivo "El costeo venció" y el usuario "ana"
    Y la cotización "C1" vuelve a consultarse en Nexo

  Escenario: Una cotización entregada se re-solicita sin cambios de carga
    Dado que Nexo costeó la solicitud de "C1" con un flete de 2550000 y la entregó
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "El cliente pidió actualizar el valor"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"

  Escenario: Con cambios de carga se crea una versión nueva y la anterior se conserva
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "El cliente cambió el peso" y estos datos de carga:
      | campo         | valor |
      | peso_kg       | 28000 |
      | tipo_vehiculo | 3S3   |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el folio de "C1" con el sufijo "-v2" y la versión 2
    Y el encabezado Location apunta al costeo de la versión nueva
    Y la versión nueva queda en el estado de costeo "pendiente_costeo" con el peso 28000 y el vehículo "3S3"
    Y la cotización "C1" sigue en el estado de costeo "costeo_vencido" sin cambios
    Y Nexo recibió la solicitud de la versión nueva con el peso 28000

  Escenario: Desde un error de envío equivale a reintentar
    Dado que Nexo respondió 503 al alta de "C1" y quedó en "error_envio"
    Cuando Nexo vuelve a responder
    Y "ana" re-solicita el costeo de "C1" con el motivo "Nexo estaba caído"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"

  Escenario: Si Nexo falla en la re-solicitud la cotización queda en error de envío
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Y que Nexo responde 503 al alta de solicitudes
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "El costeo venció"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda en el estado de costeo "error_envio" con un motivo que menciona "HTTP 503"

  Esquema del escenario: En otros estados no se permite
    Dado que la cotización "C1" está en el estado de costeo "<estado>"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Probar"
    Entonces la respuesta tiene el estado 409 con el código de negocio "ESTADO_NO_PERMITE"
    Y Nexo no recibió otra solicitud para "C1"

    Ejemplos:
      | estado           |
      | pendiente_costeo |
      | autocosteo       |
      | descartada       |

  Escenario: Con resultado registrado no se re-solicita en el mismo folio
    Dado que Nexo costeó la solicitud de "C1" con un flete de 2550000 y la entregó
    Y "ana" marcó el resultado de "C1" como "perdida" con el motivo "Precio alto"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Reintentar con otro precio"
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_ENVIADO"
    Y el detalle del error indica la acción "nueva_version" con "POST" al endpoint de re-solicitud de "C1" y nueva_version "true"

  Escenario: Ya enviada al cliente, nueva_version crea la versión sin cambios de carga
    Dado que Nexo costeó la solicitud de "C1" con un flete de 2550000 y la entregó
    Y que "ana" generó el PDF del costeo de "C1" y lo envió al cliente
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Ajustar después del envío" y nueva_version "true"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el folio de "C1" con el sufijo "-v2" y la versión 2
    Y la cotización "C1" conserva su PDF enviado sin cambios

  Escenario: En el mismo folio sin enviar, la re-solicitud invalida el PDF
    Dado que Nexo costeó la solicitud de "C1" con un flete de 2550000 y la entregó
    Y que "ana" generó el PDF del costeo de "C1"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Actualizar el costo"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y el costeo de "C1" no tiene pdf_url y está marcado con pdf_desactualizado

  Escenario: Sin motivo se rechaza
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo ""
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"

  Escenario: Cambios de carga que rompen la regla de peso de Nexo se rechazan sin crear versión
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Cambio de peso" y estos datos de carga:
      | campo   | valor |
      | peso_kg | 0     |
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y "ana" tiene 1 cotización con el folio base de "C1"

  Escenario: Otro comercial no puede re-solicitar
    Dado el comercial "beto" con sesión en la regional "Occidente"
    Cuando "beto" re-solicita el costeo de "C1" con el motivo "No es mía"
    Entonces la respuesta tiene el estado 404

  Escenario: Cambios de carga incoherentes con la captura se rechazan sin crear versión
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Cambio de contenedor" y estos datos de carga:
      | campo            | valor        |
      | clase_viaje      | Carga Suelta |
      | clase_contenedor | CNT40.HC     |
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y "ana" tiene 1 cotización con el folio base de "C1"

  Escenario: Un doble clic con la misma Idempotency-Key no crea dos versiones
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita dos veces el costeo de "C1" con el motivo "Cambio de peso", el peso 28000 y la misma Idempotency-Key
    Entonces las dos respuestas tienen el estado 201 y el mismo cuerpo
    Y "ana" tiene 2 cotizaciones con el folio base de "C1"
    Y Nexo recibió una sola solicitud nueva para el folio base de "C1"

  Escenario: Sin If-Match se exige la precondición
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "El costeo venció" sin If-Match
    Entonces la respuesta tiene el estado 428 con el código de negocio "PRECONDICION_REQUERIDA"

  Escenario: Las respuestas 200 y 201 traen el ETag nuevo
    Dado que el polling consultó a Nexo 73 horas después del envío y "C1" quedó en "costeo_vencido"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "El costeo venció"
    Entonces la respuesta trae un ETag
