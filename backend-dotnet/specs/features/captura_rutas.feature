# language: es
# HU-021.6 (#132, ADR-019) — Captura por rutas en el formulario de costeo (POST /web/solicitudes-costeo con "rutas"): cada ruta lleva su
# línea de negocio, origen y destino con id de municipio y copia del nombre (decisión 2), mercancías con su tipo del catálogo del
# cubicador, el valor del levante y sus servicios conexos del catálogo de Nexo con el costo de Nexo como precio (decisión 3). HU-032 (#197,
# ADR-022): el comercial elige el vehículo de cada ruta; sin cubicador configurado (como aquí) o sin mercancías cubicables la ruta nace con
# un camión con ese vehículo y su peso total (escenarios del vehículo: vehiculo_elegido.feature). Todas las validaciones llegan juntas en detalle.errores. Por
# HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un Nexo falso. Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosCapturaRutas.cs.
@integracion
Característica: Captura de la solicitud de costeo por rutas
  Como comercial de Botero Soto
  Quiero cotizar varias rutas en una sola solicitud, cada una con su línea de negocio, sus mercancías y sus servicios conexos
  Para que Nexo costee cada ruta y la Mesa de Costeo elija el vehículo

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el catálogo de servicios conexos de Nexo tiene el servicio "SC-CARGUE" "Cargue en puerto" con un costo de 150000

  Escenario: Cada ruta se guarda con su línea de negocio y un camión con el vehículo elegido y su peso total
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con estas rutas:
      | linea_negocio | origen_municipio_id | origen       | destino_municipio_id | destino   | peso_kg | tipo_vehiculo |
      | Importacion   | 76109               | Buenaventura | 11001                | Bogotá    | 28000   | 3S3           |
      | Exportacion   | 5001                | Medellín     | 13001                | Cartagena | 12500   | 2S3           |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la solicitud "C1" tiene estas rutas guardadas:
      | orden | linea_negocio | origen       | destino      | camiones | peso_camion | vehiculo |
      | 1     | Importacion   | Buenaventura | Bogotá, D.C. | 1        | 28000.00    | 3S3      |
      | 2     | Exportacion   | Medellín     | Cartagena    | 1        | 12500.00    | 2S3      |
    Y Nexo recibió una solicitud con 2 líneas con los tipos de vehículo "3S3, 2S3"

  Escenario: El nombre del municipio siempre sale del catálogo y un id que no existe se rechaza
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con estas rutas:
      | linea_negocio | origen_municipio_id | origen   | destino_municipio_id | destino | peso_kg | tipo_vehiculo |
      | Importacion   | 11001               | Medellín | 76109                | Cali    | 28000   | 3S3           |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la solicitud "C1" tiene estas rutas guardadas:
      | orden | linea_negocio | origen       | destino      | camiones | peso_camion | vehiculo |
      | 1     | Importacion   | Bogotá, D.C. | Buenaventura | 1        | 28000.00    | 3S3      |
    Cuando "ana" envía a costeo la solicitud "C2" del cliente "900555111" con estas rutas:
      | linea_negocio | origen_municipio_id | destino_municipio_id | peso_kg | tipo_vehiculo |
      | Importacion   | 99999               | 11001                | 28000   | 3S3           |
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo               | codigo                |
      | 0    | origen_municipio_id | MUNICIPIO_INEXISTENTE |

  Escenario: Con solo el id del municipio el nombre se copia del catálogo de Nexo
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con estas rutas:
      | linea_negocio | origen_municipio_id | destino_municipio_id | peso_kg | tipo_vehiculo |
      | Importacion   | 76109               | 11001                | 28000   | 3S3           |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la solicitud "C1" tiene estas rutas guardadas:
      | orden | linea_negocio | origen       | destino      | camiones | peso_camion | vehiculo |
      | 1     | Importacion   | Buenaventura | Bogotá, D.C. | 1        | 28000.00    | 3S3      |

  Escenario: Los servicios conexos de la ruta se guardan con el costo de Nexo como precio
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con la ruta Buenaventura a Bogotá y 2 del servicio "SC-CARGUE"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la ruta 1 de "C1" tiene el conexo "SC-CARGUE" "Cargue en puerto" con precio 150000 y cantidad 2
    Y la respuesta no trae el costo 150000

  Escenario: Todos los errores de las rutas llegan juntos y no se guarda nada
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con estas rutas:
      | linea_negocio | origen   | destino_municipio_id | destino | peso_kg | tipo_mercancia | tipo_vehiculo |
      | Importacion   | La Unión | 11001                | Bogotá  | 28000   |                | 3S3           |
      | Importacion   | Cali     | 11001                | Bogotá  |         | sin tipo       | 3S3           |
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo                            | codigo            |
      | 0    | origen                           | MUNICIPIO_AMBIGUO |
    Y Nexo no recibió solicitudes
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

  Escenario: Un servicio conexo que no está en el catálogo de Nexo es un error de su ruta
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con la ruta Buenaventura a Bogotá y 1 del servicio "SC-NO-EXISTE"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo                                 | codigo               |
      | 0    | servicios_conexos[0].id_servicio_conexo | CONEXO_NO_ENCONTRADO |
    Y Nexo no recibió solicitudes

  # HU-030.1 (#184) — Peso con tara de las cargas contenerizadas: el comercial escribe el peso total CON la tara por ruta; viaja a Nexo
  # como el peso de la línea. #250 (DU §8): con un solo camión viaja con sus posiciones (Nexo solo rechaza con PESO_INCONSISTENTE una suma
  # de posiciones que supera el peso en más de 1 %; un peso mayor, la tara, es válido). Solo en Contenedor.
  Escenario: Una ruta de contenedor con peso con tara viaja a Nexo con ese peso y sus posiciones
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con estas rutas:
      | linea_negocio | origen_municipio_id | origen       | destino_municipio_id | destino | tipologia  | clase_viaje | clase_contenedor | peso_kg | peso_con_tara_kg | tipo_mercancia  |
      | Importacion   | 76109               | Buenaventura | 11001                | Bogotá  | Contenedor | Contenedor  | CNT40.HC         | 1000    | 4800             | CAFE_EN_ESTIBAS |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo recibió una línea con el peso 4800 y sus posiciones
    Y la ruta 1 de "C1" guarda el peso con tara "4800.00" y el peso de carga "1000.00"

  Escenario: El peso con tara solo aplica a las cargas contenerizadas
    Cuando "ana" envía a costeo la solicitud "C1" del cliente "900555111" con estas rutas:
      | linea_negocio | origen_municipio_id | origen       | destino_municipio_id | destino | clase_viaje | peso_kg | peso_con_tara_kg | tipo_vehiculo |
      | Importacion   | 76109               | Buenaventura | 11001                | Bogotá  | Cupo        | 1000    | 4800             | 3S3           |
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo            | codigo          |
      | 0    | peso_con_tara_kg | CAMPO_NO_APLICA |
    Y Nexo no recibió solicitudes
