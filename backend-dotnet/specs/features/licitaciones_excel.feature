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
      | origen       | destino      | tipo_vehiculo | tipologia  | clase_viaje  | clase_contenedor | valor_aprovisionamiento |
      | BUENAVENTURA | BOGOTA, D.C. | 3S3           | CargaSeca  |              |                  |                         |
      | CALI         | CARTAGENA    | 3s3           | CargaSeca  |              |                  |                         |
      | CUCUTA       | BOGOTA, D.C. | Tractomula    | carga seca |              |                  |                         |
      | BUENAVENTURA | CALI         | 3S3           | Contenedor | Contenedor   |                  |                         |
      | CALI         | CARTAGENA    | 3S3           | CargaSeca  | Carga Suelta | CNT40.HC         | 150000                  |
      | La Unión     | CALI         | 3S3           | CargaSeca  |              |                  |                         |
      | CALI         | Macondo      | 3S3           | CargaSeca  |              |                  |                         |
    Entonces la respuesta tiene el estado 400 con el reporte de la licitación
    Y el reporte trae exactamente estos errores:
      | linea | campo                   | codigo                  |
      | 1     | tipo_vehiculo           | CODIGO_NO_CATALOGO      |
      | 2     | tipologia               | CODIGO_NO_CATALOGO      |
      | 2     | tipo_vehiculo           | CODIGO_NO_CATALOGO      |
      | 3     | clase_contenedor        | CAMPO_REQUERIDO         |
      | 4     | clase_contenedor        | CAMPO_NO_APLICA         |
      | 4     | valor_aprovisionamiento | CAMPO_NO_APLICA         |
      | 5     | origen                  | MUNICIPIO_AMBIGUO       |
      | 6     | destino                 | MUNICIPIO_NO_ENCONTRADO |
    Y Nexo no recibió ninguna solicitud

  Escenario: Las clases de contenedor provisionales de extradimensionado se aceptan en las filas
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen       | destino      | tipo_vehiculo | tipologia  | clase_viaje | clase_contenedor |
      | BUENAVENTURA | BOGOTA, D.C. | 3S3           | Contenedor | Contenedor  | CNT40.FT         |
      | BUENAVENTURA | CALI         | 3S3           | Contenedor | Contenedor  | CNT20.OP         |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "Licitacion" del contrato
    Y la línea 2 que recibió Nexo trae "claseContenedor" igual a "CNT20.OP"

  Escenario: Nexo recibe por línea los ids de municipio y las horas, y el reintento manda lo mismo
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen           | destino      | tipo_vehiculo | tipologia | horas_cargue | horas_descargue |
      | BUENAVENTURA     | BOGOTA, D.C. | 3S3           | CargaSeca | 2            | 3.5             |
      | La Unión, Nariño | CALI         | 3S3           | CargaSeca |              |                 |
    Entonces la licitación "L1" queda en el estado "error_envio"
    Cuando Nexo vuelve a responder
    Y "ana" reintenta el envío de la licitación "L1"
    Entonces la licitación "L1" queda en el estado "pendiente_costeo"
    Y la línea 1 que recibió Nexo trae los municipios 76109 y 11001 con 2 horas de cargue y 3.5 de descargue
    Y la línea 2 que recibió Nexo trae los municipios 52399 y 76001 sin horas propias
    Y el primer envío y el reintento mandaron a Nexo la misma solicitud

  Escenario: Sin catálogo maestro de Nexo la licitación no se guarda
    Cuando Nexo deja de responder
    Y "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"
    Y Nexo no recibió ninguna solicitud

  Escenario: Sin catálogo de municipios las filas sin id no se pueden resolver y la licitación no se guarda
    Dado que el catálogo de municipios de Nexo no responde
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Entonces la respuesta tiene el estado 503 con el código de negocio "MUNICIPIOS_NO_DISPONIBLE"
    Y Nexo no recibió ninguna solicitud
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

  Escenario: Sin catálogo de municipios una licitación con los ids de cada fila se crea igual
    Dado que el catálogo de municipios de Nexo no responde
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con estas filas:
      | origen       | destino      | origen_municipio_id | destino_municipio_id | tipo_vehiculo | tipologia |
      | BUENAVENTURA | BOGOTA, D.C. | 76109               | 11001                | 3S3           | CargaSeca |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "Licitacion" del contrato
    Y la línea 1 que recibió Nexo trae los municipios 76109 y 11001 sin horas propias
