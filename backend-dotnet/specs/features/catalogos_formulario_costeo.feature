# language: es
# HU-013.1 (#97, ADR-018), HU-013.10 (#106) y docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md — Catálogos del formulario de costeo. POST
# /api/captura-solicitudes de Nexo no valida contra sus catálogos: resuelve el texto por coincidencia aproximada y, si no
# encuentra el tipo de vehículo, crea una fila nueva de catálogo (duplicados). Cotifly ofrece las opciones de los catálogos
# maestros de Nexo (GET /api/catalogos/*, con caché) en lugar del histórico de tarifas (biblioteca-sap-historico/filtros) y
# envía a Nexo solo códigos de catálogo. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un
# Nexo falso. Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosCatalogosCosteo.cs.
@integracion
Característica: Solo códigos de catálogo en el formulario de costeo
  Como comercial de Botero Soto
  Quiero elegir el vehículo y la clase de viaje de los catálogos maestros de Nexo
  Para que Nexo costee la ruta correcta sin crear vehículos duplicados

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el catálogo maestro de tipos de vehículo de Nexo tiene:
      | codigo | descripcion                     | esCatalogoOficial |
      | 2S3    | Tractocamión 2 ejes, semi 3     | true              |
      | 3S3    | Tractocamión 3 ejes, semi 3     | true              |
    Y el catálogo maestro de clases de viaje de Nexo tiene "Contenedor", "Cupo", "Carga Suelta" y "Extradimensionado"

  Escenario: Los catálogos del formulario salen de los catálogos maestros de Nexo
    Cuando "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CatalogosFlete" del contrato
    Y los tipos de vehículo son "2S3" y "3S3" con su descripción como etiqueta
    Y las clases de viaje son "Contenedor", "Cupo", "Carga Suelta" y "Extradimensionado"
    Y Nexo recibió la consulta de "/api/catalogos/tipos-vehiculo" y de "/api/catalogos/clases-viaje"
    Y Nexo no recibió la consulta de "/api/biblioteca-sap-historico/filtros"

  Escenario: Un tipo de vehículo del catálogo viaja a Nexo con su código tal cual
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con el tipo de vehículo "3S3"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo recibió la solicitud de "C1" con el tipoVehiculo "3S3"

  Esquema del escenario: Un tipo de vehículo que no es código de catálogo se rechaza y no llega a Nexo
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con el tipo de vehículo "<texto>"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el mensaje del error menciona "tipo_vehiculo"
    Y Nexo no recibió solicitudes
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

    Ejemplos:
      | texto                       |
      | Tractomula                  |
      | tractocamión 3 ejes semi 3  |
      | 3s3                         |
      | 3S3 refrigerado             |

  Escenario: Una tipología fuera del enum de Nexo se rechaza
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la tipología "Mercancía general"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y Nexo no recibió solicitudes

  Escenario: Clase de contenedor sin clase de viaje Contenedor se rechaza
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Carga Suelta" y la clase de contenedor "CNT40.HC"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y Nexo no recibió solicitudes

  Escenario: Con el catálogo maestro caído el formulario no ofrece texto libre
    Cuando Nexo deja de responder
    Y "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"

  Escenario: Con el catálogo maestro caído y sin copia en memoria la solicitud no se guarda ni llega a Nexo
    Cuando Nexo deja de responder
    Y "ana" envía a costeo el formulario del cliente "900555111" como "C1" con el tipo de vehículo "3S3"
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

  Esquema del escenario: Las clases de contenedor provisionales de Nexo no se rechazan
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Contenedor" y la clase de contenedor "<clase>"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo recibió la solicitud de "C1" con el claseContenedor "<clase>"

    Ejemplos:
      | clase    |
      | CNT20.FT |
      | CNT20.OP |
      | CNT40.FT |
      | CNT40.OP |
