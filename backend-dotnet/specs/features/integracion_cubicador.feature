# language: es
# HU-021.5 (#133, ADR-019 decisión 7) — Integración con el cubicador: por cada ruta de la captura Cotifly pide al cubicador
# (POST /recomendaciones, con IdLineaSolicitud = id de la ruta y OrigenSistema = COTIFLY) los vehículos y crea un camión por fila de la
# respuesta, con su vehículo, su carrocería, su peso total (C2) y el id de la recomendación (C3). "Sin selección automática" y
# "condicionada" son resultados, no errores (C6). Si el cubicador no está configurado o falla, la ruta queda con un camión sin vehículo
# (respaldo, con Cubicador:RespaldoSiFalla). El nombre de cada mercancía se copia del catálogo del cubicador (GET /tipos-mercancia).
# El cubicador real aún no tiene estos endpoints (C1): por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers, el
# Nexo falso y el cubicador falso (nunca los reales). Pasos en Cotifly.Aceptacion.Tests/Costeo/clsPasosCubicador.cs.
@integracion
Característica: Camiones de cada ruta según el cubicador
  Como comercial de Botero Soto
  Quiero que el cubicador defina los vehículos de cada ruta
  Para que Nexo costee cada camión con su vehículo y su peso, sin que yo los digite

  Antecedentes:
    Dado que el cubicador tiene el tipo de mercancía "Café en estibas" de las mercancías del formulario
    Y la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y al cubicador
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: El cubicador recomienda dos camiones para la ruta y cada uno se costea en Nexo
    Dado que el cubicador recomienda para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg | resultado    |
      | 3S3      | FURGON     | 16000   | SELECCIONADO |
      | 2S3      | ESTACAS    | 12500   | SELECCIONADO |
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces el cubicador recibió la ruta 1 de "C1" con el origen "COTIFLY" y su llave como Bearer
    Y cada camión de la ruta 1 de "C1" guarda el id de su recomendación del cubicador
    Y las mercancías de "C1" se guardaron con el nombre "Café en estibas"
    Y Nexo recibió 2 líneas de "Buenaventura" a "Bogotá, D.C.", una por camión y sin posiciones
    Y las líneas que recibió Nexo llevan los pesos "16000, 12500"
    Cuando Nexo devuelve las líneas de la solicitud en orden aleatorio
    Y Nexo costea la línea del camión de 16000 kg de "C1" con un flete de 1500000
    Y Nexo costea la línea del camión de 12500 kg de "C1" con un flete de 1000000
    Y el polling consulta a Nexo
    Entonces cada camión de "C1" quedó asociado a una línea distinta de Nexo
    Y el camión de 16000 kg de "C1" tiene el vehículo "3S3" y la carrocería "FURGON"
    Y el camión de 16000 kg de "C1" tiene el flete 1500000 y el estado "autocosteo"
    Y el camión de 12500 kg de "C1" tiene el vehículo "2S3" y la carrocería "ESTACAS"
    Y el camión de 12500 kg de "C1" tiene el flete 1000000 y el estado "autocosteo"

  Escenario: Sin selección automática el camión viaja sin vehículo para que lo resuelva la Mesa de Costeo
    Dado que el cubicador recomienda para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg | resultado                |
      |          |            | 28500   | SIN_SELECCION_AUTOMATICA |
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y cada camión de la ruta 1 de "C1" guarda el id de su recomendación del cubicador
    Y ninguna de las líneas que recibió Nexo lleva vehículo ni carrocería

  Escenario: Si el cubicador no responde la ruta queda con un camión sin vehículo con el peso total
    Dado que el cubicador no responde
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y la ruta 1 de "C1" tiene un solo camión sin vehículo ni recomendación de 28500 kg
    Y ninguna de las líneas que recibió Nexo lleva vehículo ni carrocería
