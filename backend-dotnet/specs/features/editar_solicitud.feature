# language: es
# HU-028 (#186, ADR-021) — El comercial edita la solicitud: todo menos el cliente, el tipo (spot/licitación) y la línea de negocio de
# cada ruta. GET /web/cotizaciones/{folio}/solicitud devuelve el modelo editable con su ETag; PUT con ese modelo cambiado aplica la
# edición según el estado:
#   - en_sitio: nunca llegó a Nexo; se edita y se envía;
#   - reemplazo: está en Nexo sin entregar al cliente ni resultado; Cotifly rechaza la solicitud anterior en Nexo y captura de nuevo
#     en el mismo folio (si el rechazo falla, no captura otra);
#   - nueva_version: ya entregada o con resultado; se crea la -vN y la original no cambia.
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y el Nexo falso (nunca el real). Pasos en
# Cotifly.Aceptacion.Tests/Costeo/clsPasosEdicionSolicitud.cs.
@integracion
Característica: Editar la solicitud
  Como comercial de Botero Soto
  Quiero corregir la solicitud que ya envié
  Para que Nexo costee lo que el cliente pidió sin crear otra cotización

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Sin llegar a Nexo la solicitud se edita en sitio y se envía
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo vuelve a responder
    Y "ana" edita la solicitud "C1" con las horas de cargue en 9
    Entonces la respuesta tiene el estado 200 y cumple el esquema "EdicionSolicitudResultado" del contrato
    Y la edición de "C1" se aplicó como "en_sitio" en el mismo folio
    Y Nexo no recibió ningún rechazo
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y la ruta 1 de "C1" tiene 9 horas de cargue

  Escenario: En Nexo y sin entregar la edición reemplaza la solicitud en el mismo folio
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" edita la solicitud "C1" con las horas de cargue en 9
    Entonces la respuesta tiene el estado 200 y cumple el esquema "EdicionSolicitudResultado" del contrato
    Y la edición de "C1" se aplicó como "reemplazo" en el mismo folio
    Y Nexo rechazó la solicitud anterior de "C1" con el motivo "Edición de la solicitud desde Cotifly"
    Y la cotización "C1" apunta a una solicitud nueva de Nexo
    Y la ruta 1 de "C1" tiene 9 horas de cargue

  Escenario: Si Nexo no deja rechazar la solicitud anterior no se captura otra
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo no deja rechazar solicitudes
    Y "ana" edita la solicitud "C1" con las horas de cargue en 9
    Entonces la respuesta tiene el estado 503 con el código de negocio "NEXO_NO_DISPONIBLE"
    Y Nexo tiene una sola solicitud de "C1" y sigue viva
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"

  Escenario: Ya con resultado la edición crea una versión nueva y la original no cambia
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Dado que Nexo costeó la solicitud de "C1" con un flete de 2550000 y la entregó
    Y "ana" marcó el resultado de "C1" como "ganada" con el motivo "Precio competitivo"
    Cuando "ana" edita la solicitud "C1" con las horas de cargue en 9
    Entonces la respuesta tiene el estado 201 y cumple el esquema "EdicionSolicitudResultado" del contrato
    Y la edición de "C1" se aplicó como "nueva_version" en el folio con el sufijo "-v2"
    Y Nexo no recibió ningún rechazo
    Y la cotización "C1" queda en el estado de costeo "entregada"

  Escenario: Cambiar el cliente al editar no se permite
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" edita la solicitud "C1" con el NIT "800111222"
    Entonces la respuesta tiene el estado 400 con el código de negocio "CAMPO_NO_EDITABLE"
    Y Nexo no recibió ningún rechazo

  Escenario: Una licitación no se edita en esta fase
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 2 filas mapeadas
    Y "ana" consulta para editar la solicitud de la licitación "L1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "SolicitudEditable" del contrato
    Y la edición no se permite por "LICITACION"
