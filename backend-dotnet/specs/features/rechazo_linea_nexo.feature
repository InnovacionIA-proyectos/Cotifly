# language: es
# HU-028.1 (#183, Nexo #320) — La Mesa de Costeo de Nexo rechaza una línea (un camión de Cotifly) con un motivo. Cotifly lo lee con el
# polling (estadoLinea = "rechazada" y motivoRechazo de GET /api/solicitudes/{id}), lo guarda en el camión (db/scripts/10) y lo refleja en
# todo: la ruta rechazada no tiene precio, no suma en el total, no admite margen ni conexos (409 RUTA_RECHAZADA), sale "Sin costeo" (sin
# motivo) en el PDF y en el Excel, y no deja la cotización "pendiente" para siempre. Mientras la cotización no esté entregada la Mesa puede
# deshacerlo. Con todas las rutas rechazadas la cotización queda entregada, sin total, y el PDF responde 409 SIN_RUTAS_COTIZABLES (D1).
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y el Nexo falso (nunca el real).
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosRechazoLinea.cs; los de la licitación se reutilizan.
@integracion
Característica: Rechazo de una línea en la Mesa de Costeo de Nexo
  Como comercial de Botero Soto
  Quiero ver qué líneas rechazó la Mesa de Costeo y por qué
  Para cotizarle al cliente solo lo que Nexo costeó, sin esperar una línea que nunca tendrá precio

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Una ruta rechazada no cuenta en el precio, sale "Sin costeo" en el PDF y el Excel y no admite margen
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y la Mesa de Costeo de Nexo rechaza la línea 3 de "L1" con el motivo "Sin operación en la ruta"
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado de costeo "autocosteo" con 2 líneas con precio
    Y la línea 3 de "L1" está rechazada con el motivo "Sin operación en la ruta" y sin precio
    Y el precio final de "L1" es la suma de sus líneas con precio
    Y el camión de la ruta 3 de "L1" quedó guardado como rechazado con el motivo "Sin operación en la ruta"
    Cuando "ana" ajusta el margen de la ruta 3 de la licitación "L1" a "0.20"
    Entonces la respuesta tiene el estado 409 con el código de negocio "RUTA_RECHAZADA"
    Cuando "ana" exporta el Excel de la licitación "L1"
    Entonces el Excel trae el precio final de las líneas 1 y 2 y marca la línea 3 como "SIN COSTEO"
    Y el Excel no contiene el texto "Sin operación en la ruta"
    Cuando "ana" genera el PDF de la licitación "L1"
    Entonces el PDF del pliego contiene "Sin costeo"
    Y el PDF del pliego no contiene el texto "Sin operación en la ruta"

  Escenario: Si la Mesa deshace el rechazo antes de entregar, la línea vuelve a pendiente y se costea
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y la Mesa de Costeo de Nexo rechaza la línea 3 de "L1" con el motivo "Revisar tarifa"
    Y el polling consulta a Nexo por "L1"
    Y la Mesa de Costeo de Nexo deshace el rechazo de la línea 3 de "L1"
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado de costeo "pendiente_costeo" con 2 líneas con precio
    Y el camión de la ruta 3 de "L1" quedó guardado como "pendiente_costeo" sin motivo de rechazo
    Cuando Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado de costeo "autocosteo" con 3 líneas con precio

  Escenario: Con todas las rutas rechazadas la cotización queda entregada sin total y el PDF se bloquea
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y la Mesa de Costeo de Nexo rechaza las 3 líneas de "L1" con el motivo "Cliente fuera de cobertura"
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado "pendiente_costeo"
    Cuando Nexo entrega la cotización de la licitación "L1"
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado "entregada"
    Y la licitación "L1" no tiene precio final y avisa que la Mesa rechazó todas las rutas
    Cuando "ana" intenta generar el PDF de la licitación "L1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "SIN_RUTAS_COTIZABLES"

  # Decisión del 2026-10-20: "no reenviar los camiones rechazados por defecto". La ruta toda rechazada no viaja (no se manda a Nexo una ruta
  # sin camiones): queda visible, rechazada y sin precio, y la respuesta lo avisa (aviso_rechazos). Para reconsiderarla, el comercial edita la
  # solicitud con reincluir_rechazados (PUT /web/cotizaciones/{folio}/solicitud).
  Escenario: Al re-solicitar, la ruta rechazada no viaja a Nexo, queda rechazada y la respuesta avisa qué se omitió
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y la Mesa de Costeo de Nexo rechaza la línea 3 de "L1" con el motivo "Sin operación en la ruta"
    Y Nexo entrega la cotización de la licitación "L1"
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado "entregada"
    Cuando "ana" re-solicita el costeo de la licitación "L1" con el motivo "Actualizar tarifas"
    Entonces la respuesta avisa que no se reenvió la ruta 3 con el motivo "Sin operación en la ruta"
    Y Nexo recibió la solicitud nueva de "L1" con 2 líneas
    Y la línea 3 de "L1" sigue rechazada con el motivo "Sin operación en la ruta" y sin línea en Nexo
    Cuando Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado de costeo "autocosteo" con 2 líneas con precio
    Y la línea 3 de "L1" está rechazada con el motivo "Sin operación en la ruta" y sin precio
