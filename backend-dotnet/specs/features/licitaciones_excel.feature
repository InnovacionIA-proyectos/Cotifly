# language: es
# HU-011.2 (ADR-017) — Licitaciones desde el Excel del cliente: análisis en Nexo, mapeo en el frontend, validación por fila,
# envío a Nexo como UNA solicitud con N líneas y polling línea por línea. Por HTTP contra la API real en memoria, SQL Server 2019
# en Testcontainers y un Nexo falso (nunca el real).
@integracion
Característica: Licitaciones desde Excel
  Como comercial de Botero Soto
  Quiero cargar el pliego de una licitación tal como lo manda el cliente
  Para cotizar todas sus líneas en Nexo sin copiarlas a mano

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Cotifly reenvía el pliego al análisis de Nexo sin guardar nada
    Cuando "ana" analiza el pliego de 3 filas
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AnalisisExcel" del contrato
    Y Nexo recibió el archivo para analizarlo

  Escenario: La licitación llega a Nexo como una sola solicitud con todas sus líneas
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la respuesta tiene el estado 201 y cumple el esquema "Licitacion" del contrato
    Y Nexo recibió una solicitud de licitación con 3 líneas y el BP "15927"
    Y la licitación "L1" tiene 3 líneas en el estado de costeo "pendiente_costeo"
    Y el Excel original de "L1" quedó guardado

  Escenario: Las filas inválidas se reportan todas y no se envía nada a Nexo
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con una fila sin destino y otra sin peso
    Entonces la respuesta tiene el estado 400 con el reporte de la licitación
    Y el reporte trae el código "LINEA_INCOMPLETA" en la línea 0 y el código "PESO_REQUERIDO" en la línea 2
    Y Nexo no recibió ninguna solicitud

  Escenario: El polling costea la licitación línea por línea
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado de costeo "pendiente_costeo" con 2 líneas con precio
    Cuando Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Entonces la licitación "L1" queda en el estado de costeo "autocosteo" con 3 líneas con precio
    Y el precio final de "L1" es la suma de sus líneas

  # HU-011.3 — Exportación del Excel con resultados
  Escenario: El Excel exportado conserva el pliego del cliente, agrega los precios y marca las líneas sin costeo
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" exporta el Excel de la licitación "L1"
    Entonces el Excel conserva las columnas originales del pliego
    Y el Excel trae el precio final de las líneas 1 y 2 y marca la línea 3 como "SIN COSTEO"
    Y el Excel no contiene el costo 1000000 de Nexo

  Escenario: Si Nexo no responde la licitación no se pierde y se reintenta con todas sus líneas
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la licitación "L1" queda en el estado "error_envio"
    Cuando Nexo vuelve a responder
    Y "ana" reintenta el envío de la licitación "L1"
    Entonces la licitación "L1" queda en el estado "pendiente_costeo"
    Y Nexo recibió una solicitud de licitación con 3 líneas y el BP "15927"
