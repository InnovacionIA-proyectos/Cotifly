# language: es
# HU-021.5 (#133, ADR-019 decisión 7) y HU-023.1 (#156, contrato del cubicador) — Integración con el cubicador: por cada ruta de la
# captura Cotifly pide al cubicador (POST /recomendaciones, con idLineaSolicitud = id de la ruta, origenSistema = COTIFLY, la clase de
# viaje como preferencia, el comercial y cada mercancía con su codigoMercancia) los vehículos y crea un camión por vehículo de la
# respuesta ASIGNADO (flota mixta), con su código de vehículo y de carrocería, el peso que lleva, el volumen, los dos porcentajes de
# llenado y el id de la recomendación. SIN_SELECCION_AUTOMATICA (con su motivo) y "condicionada" son resultados, no errores. Si el
# cubicador no está configurado o se cae, la ruta queda con un camión sin vehículo (respaldo, con Cubicador:RespaldoSiFalla); un
# rechazo o una respuesta incompleta nunca van al respaldo. El nombre de cada mercancía se copia del catálogo (GET /tipos-mercancia).
# El cubicador real aún no está publicado: por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers, el Nexo falso y
# el cubicador falso (nunca los reales). Pasos en Cotifly.Aceptacion.Tests/Costeo/clsPasosCubicador.cs.
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
    Dado que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 3S3      | FURGON     | 16000   |
      | C2       | ESTACAS    | 12500   |
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
    Y el camión de 12500 kg de "C1" tiene el vehículo "C2" y la carrocería "ESTACAS"
    Y el camión de 12500 kg de "C1" tiene el flete 1000000 y el estado "autocosteo"

  Escenario: Sin selección automática el camión viaja sin vehículo para que lo resuelva la Mesa de Costeo
    Dado que el cubicador no puede escoger vehículo por "SIN_CARROCERIA_COMPATIBLE"
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y la ruta 1 de "C1" tiene un solo camión sin vehículo ni recomendación de 28500 kg
    Y ninguna de las líneas que recibió Nexo lleva vehículo ni carrocería

  Escenario: Si el cubicador no responde la ruta queda con un camión sin vehículo con el peso total
    Dado que el cubicador no responde
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y la ruta 1 de "C1" tiene un solo camión sin vehículo ni recomendación de 28500 kg
    Y ninguna de las líneas que recibió Nexo lleva vehículo ni carrocería
