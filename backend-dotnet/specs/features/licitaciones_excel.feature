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

  # HU-011.5 — PDF de pliego
  Escenario: Con todas las líneas costeadas el PDF sale con el resumen del pliego y sin costos de Nexo
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" genera el PDF de la licitación "L1"
    Entonces el PDF del pliego trae el resumen de 3 rutas con su total
    Y el PDF del pliego no contiene el costo 1000000 de Nexo

  Escenario: Si Nexo no responde la licitación no se pierde y se reintenta con todas sus líneas
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la licitación "L1" queda en el estado "error_envio"
    Cuando Nexo vuelve a responder
    Y "ana" reintenta el envío de la licitación "L1"
    Entonces la licitación "L1" queda en el estado "pendiente_costeo"
    Y Nexo recibió una solicitud de licitación con 3 líneas y el BP "15927"

  # HU-013.11 (#108) — Cada fila cumple las reglas de captura del formulario: códigos de catálogo (G2), reglas condicionales de
  # contenedor y aprovisionamiento (G3), ids de municipio resueltos sin adivinar (G6) y horas por línea (G5).
  Escenario: Las filas con texto libre, reglas de contenedor rotas y municipios sin resolver se reportan todas juntas
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen       | destino      | tipologia  | clase_viaje  | clase_contenedor | valor_aprovisionamiento |
      | BUENAVENTURA | BOGOTA, D.C. | CargaSeca  |              |                  |                         |
      | CALI         | CARTAGENA    | CargaSeca  | Barco        |                  |                         |
      | CUCUTA       | BOGOTA, D.C. | carga seca |              |                  |                         |
      | BUENAVENTURA | CALI         | Contenedor | Contenedor   |                  |                         |
      | CALI         | CARTAGENA    | CargaSeca  | Carga Suelta | CNT40.HC         | 150000                  |
      | La Unión     | CALI         | CargaSeca  |              |                  |                         |
      | CALI         | Macondo      | CargaSeca  |              |                  |                         |
    Entonces la respuesta tiene el estado 400 con el reporte de la licitación
    Y el reporte trae exactamente estos errores:
      | linea | campo                   | codigo                  |
      | 1     | clase_viaje             | CODIGO_NO_CATALOGO      |
      | 2     | tipologia               | CODIGO_NO_CATALOGO      |
      | 3     | clase_contenedor        | CAMPO_REQUERIDO         |
      | 4     | clase_contenedor        | CAMPO_NO_APLICA         |
      | 4     | valor_aprovisionamiento | CAMPO_NO_APLICA         |
      | 5     | origen                  | MUNICIPIO_AMBIGUO       |
      | 6     | destino                 | MUNICIPIO_NO_ENCONTRADO |
    Y Nexo no recibió ninguna solicitud

  Escenario: Las clases de contenedor provisionales de extradimensionado se aceptan en las filas
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen       | destino      | tipologia  | clase_viaje | clase_contenedor |
      | BUENAVENTURA | BOGOTA, D.C. | Contenedor | Contenedor  | CNT40.FT         |
      | BUENAVENTURA | CALI         | Contenedor | Contenedor  | CNT20.OP         |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "Licitacion" del contrato
    Y la línea 2 que recibió Nexo trae "claseContenedor" igual a "CNT20.OP"

  Escenario: Nexo recibe por línea los ids de municipio y las horas, y el reintento manda lo mismo
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen           | destino      | tipologia | horas_cargue | horas_descargue |
      | BUENAVENTURA     | BOGOTA, D.C. | CargaSeca | 2            | 3.5             |
      | La Unión, Nariño | CALI         | CargaSeca |              |                 |
    Entonces la licitación "L1" queda en el estado "error_envio"
    Cuando Nexo vuelve a responder
    Y "ana" reintenta el envío de la licitación "L1"
    Entonces la licitación "L1" queda en el estado "pendiente_costeo"
    Y la línea 1 que recibió Nexo trae los municipios 76109 y 11001 con 2 horas de cargue y 3.5 de descargue
    Y la línea 2 que recibió Nexo trae los municipios 52399 y 76001 sin horas propias
    Y el primer envío y el reintento mandaron a Nexo la misma solicitud

  Escenario: Sin catálogo maestro de Nexo la licitación no se guarda
    Cuando Nexo deja de responder
    Y "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen       | destino      | tipologia | clase_viaje |
      | BUENAVENTURA | BOGOTA, D.C. | CargaSeca | Cupo        |
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"
    Y Nexo no recibió ninguna solicitud

  Escenario: Sin catálogo de municipios las filas sin id no se pueden resolver y la licitación no se guarda
    Dado que el catálogo de municipios de Nexo no responde
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la respuesta tiene el estado 503 con el código de negocio "MUNICIPIOS_NO_DISPONIBLE"
    Y Nexo no recibió ninguna solicitud
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

  # HU-021.6 (decisión 2): el nombre del municipio siempre se copia del catálogo por su id, así que sin catálogo tampoco se guarda.
  Escenario: Sin catálogo de municipios una licitación con los ids de cada fila tampoco se guarda
    Dado que el catálogo de municipios de Nexo no responde
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen       | destino      | origen_municipio_id | destino_municipio_id | tipologia |
      | BUENAVENTURA | BOGOTA, D.C. | 76109               | 11001                | CargaSeca |
    Entonces la respuesta tiene el estado 503 con el código de negocio "MUNICIPIOS_NO_DISPONIBLE"
    Y Nexo no recibió ninguna solicitud

  Escenario: El nombre del municipio se copia del catálogo y un id que no existe se rechaza
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen   | destino | origen_municipio_id | destino_municipio_id | tipologia |
      | Medellín | Cali    | 11001               | 99999                | CargaSeca |
    Entonces la respuesta tiene el estado 400 con el reporte de la licitación
    Y el reporte trae exactamente estos errores:
      | linea | campo                | codigo                |
      | 0     | destino_municipio_id | MUNICIPIO_INEXISTENTE |
    Y Nexo no recibió ninguna solicitud

  # HU-013.15 (#124) — Numeración de filas: la que ve el comercial en su Excel (número de fila de la hoja, encabezado incluido). La
  # `fila` de cada línea sigue siendo el índice (base 0) en hojas[h].filas; los mensajes nunca lo muestran. Hallazgo E2E: "La fila 3
  # no existe (3 filas con datos)" con fila = 3 contaba desde 0 en el campo y desde 1 en el mensaje.
  Escenario: El análisis trae el número de fila del Excel de cada fila del pliego
    Dado que Nexo analiza el pliego de 3 filas de la hoja "Pliego 2026"
    Cuando "ana" analiza el pliego de 3 filas
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AnalisisExcel" del contrato
    Y la hoja "Pliego 2026" del análisis trae las filas del Excel 4, 5 y 7

  Escenario: Los errores de las filas usan la numeración del Excel del comercial
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas, la última sin peso y una línea de más
    Entonces la respuesta tiene el estado 400 con el reporte de la licitación
    Y el reporte trae estos errores con la fila del Excel:
      | linea | codigo          | fila_excel |
      | 2     | PESO_REQUERIDO  | 7          |
      | 3     | FILA_INVALIDA   |            |
    Y el mensaje del error de la línea 3 es "La línea 4 apunta a una fila que no está en la hoja 'Pliego 2026': la hoja tiene 3 filas con datos (de la fila 4 a la 7 del Excel)."
    Y Nexo no recibió ninguna solicitud

  # HU-013.15 (#124) — "Licitaciones" existe en el catálogo de líneas de Nexo pero no es una línea de Cotifly (no da margen).
  Escenario: La línea "Licitaciones" de Nexo se rechaza con un error que dice qué líneas usar
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas y la línea de negocio "Licitaciones"
    Entonces la respuesta tiene el estado 400 con el código de negocio "MARGEN_LINEA_DESCONOCIDA"
    Y el detalle del error tiene el motivo "LINEA_DE_NEXO"
    Y el detalle del error trae la línea válida "Nacional"
    Y Nexo no recibió ninguna solicitud

  # HU-013.15 (#124) — El detalle de un error de Nexo lleva solo su código, nunca su texto crudo (como el resto de los endpoints).
  Escenario: Si Nexo rechaza la sesión el análisis responde 503 solo con el código de Nexo
    Dado que Nexo responde 401 con el código "SESION_INVALIDA" al análisis del Excel
    Cuando "ana" analiza el pliego de 3 filas
    Entonces la respuesta tiene el estado 503 con el código de negocio "COSTEO_NEXO_NO_DISPONIBLE"
    Y el detalle del error tiene el motivo "SESION_INVALIDA"
