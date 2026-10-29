# language: es
# HU-018.1 (#155, parte B) — Motivo de pérdida de una lista. Al marcar una cotización como perdida el motivo es obligatorio y sale de una
# lista configurable (Resultado:MotivosPerdida; por defecto la de NEXO: Valor muy alto, Flete fuera de rango de plaza, Cliente desistió
# del servicio, Tiempo de respuesta vencido y Otro). "Otro" exige un comentario. Se valida en POST .../resultado y en POST .../seguimiento
# (que sigue siendo el camino de las del chat, las descartadas y las de costeo vencido). A NEXO van el motivo de la lista y el comentario
# aparte, como antes; Cotifly guarda el motivo (con "Otro", "Otro: comentario"). Las perdidas guardadas antes, con motivo libre o sin
# motivo, siguen siendo válidas. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un Nexo falso.
@integracion
Característica: Motivo de pérdida de la lista de NEXO
  Como comercial de Botero Soto
  Quiero marcar una cotización perdida con un motivo de la lista de NEXO
  Para que la Gerencia Comercial pueda agrupar las pérdidas por motivo

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: La lista de motivos de pérdida es la de NEXO y solo Otro exige comentario
    Cuando "ana" consulta los motivos de pérdida
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MotivosPerdida" del contrato
    Y los motivos de pérdida son "Valor muy alto|Flete fuera de rango de plaza|Cliente desistió del servicio|Tiempo de respuesta vencido|Otro"
    Y solo el motivo "Otro" exige comentario

  Escenario: Un motivo fuera de la lista se rechaza sin llamar a Nexo
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "Precio alto" y el comentario "-"
    Entonces la respuesta tiene el estado 400 con el código de negocio "MOTIVO_NO_PERMITIDO"
    Y el detalle del error lista los 5 motivos permitidos
    Y Nexo no recibió ningún resultado de "C1"
    Y la cotización "C1" sigue sin resultado

  Escenario: Con el motivo Otro el comentario es obligatorio
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "Otro" y el comentario ""
    Entonces la respuesta tiene el estado 400 con el código de negocio "MOTIVO_NO_PERMITIDO"
    Y el detalle del error pide el comentario
    Y Nexo no recibió ningún resultado de "C1"

  Escenario: Con el motivo Otro y su comentario Nexo recibe el motivo y el comentario aparte
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "Otro" y el comentario "El cliente cerró la planta"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo registró la cotización de "C1" como "Perdida" con el motivo "Otro" y el comentario "El cliente cerró la planta"
    Y la cotización "C1" queda ganada "false" con el motivo "Otro: El cliente cerró la planta"

  Escenario: El motivo se acepta sin distinguir mayúsculas y viaja con la escritura de la lista
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "  valor MUY alto " y el comentario "Pidió 5 % menos"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo registró la cotización de "C1" como "Perdida" con el motivo "Valor muy alto" y el comentario "Pidió 5 % menos"
    Y la cotización "C1" queda ganada "false" con el motivo "Valor muy alto"

  # HU-033 (#199): el resultado pendiente de sincronizar guarda "Otro: comentario" y a Nexo va "Otro" con el comentario aparte.
  Escenario: Una perdida con Otro pendiente de sincronizar llega a Nexo con el motivo y el comentario aparte
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costea la solicitud de "C1" con un flete de 2550000
    Y el polling consulta a Nexo
    Y "ana" marca el resultado de "C1" como "perdida" con el motivo "Otro" y el comentario "Compró con la competencia"
    Entonces la respuesta trae la sincronización del resultado "pendiente"
    Y la cotización "C1" queda ganada "false" con el motivo "Otro: Compró con la competencia"
    Cuando Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Entonces Nexo registró la cotización de "C1" como "Perdida" con el motivo "Otro" y el comentario "Compró con la competencia"
    Y el resultado de "C1" queda "sincronizado" con Nexo

  # HU-013.17 (#127): la perdida solo en Cotifly (antes del costeo) también exige un motivo de la lista.
  Escenario: Si el cliente desiste antes del costeo el motivo también es de la lista
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" marca "C1" como perdida porque "El cliente desistió"
    Entonces la respuesta tiene el estado 400 con el código de negocio "MOTIVO_NO_PERMITIDO"
    Cuando "ana" marca "C1" como perdida porque "Cliente desistió del servicio"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda ganada "false" con el motivo "Cliente desistió del servicio"

  # La excepción de HU-005.14 se conserva: una descartada en Nexo se marca perdida con /seguimiento, con un motivo de la lista.
  Escenario: Una cotización descartada en Nexo se marca perdida con el seguimiento y un motivo de la lista
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo descarta la solicitud de "C1"
    Y el polling consulta a Nexo
    Y "ana" marca con el seguimiento la cotización "C1" como perdida con el motivo "Precio alto" y el comentario ""
    Entonces la respuesta tiene el estado 400 con el código de negocio "MOTIVO_NO_PERMITIDO"
    Cuando "ana" marca con el seguimiento la cotización "C1" como perdida con el motivo "Otro" y el comentario "Se fue con la competencia"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda ganada "false" con el motivo "Otro: Se fue con la competencia"

  Escenario: Una perdida guardada antes de la lista sin motivo sigue siendo válida
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la cotización "C1" quedó perdida sin motivo antes de la lista de motivos
    Y "ana" consulta sus cotizaciones por la API
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MisCotizaciones" del contrato
    Y la cotización "C1" queda ganada "false" sin motivo
